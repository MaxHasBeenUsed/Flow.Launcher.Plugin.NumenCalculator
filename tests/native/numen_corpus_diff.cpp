#include "abi_loader.hpp"
#include "mock_provider.hpp"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <string_view>
#include <system_error>
#include <utility>
#include <vector>

#ifndef NUMEN_C_CORPUS_DIR
#error "NUMEN_C_CORPUS_DIR must point at numen/tests/corpus"
#endif

namespace {

using numen_test::AbiLoadError;
using numen_test::NumenApi;

// Exit-code contract: 0 = pass, 2 = semantic mismatch, 3 = infrastructure.
class InfrastructureError : public std::runtime_error {
public:
  using std::runtime_error::runtime_error;
};

struct Context {
  NumenApi &api;
  numen_instance *instance;

  ~Context() {
    if (instance != nullptr) { api.fn.destroy(instance); }
  }
};

struct Outcome {
  numen_status status = NUMEN_ERR_INTERNAL;
  std::string display;
  std::string error;
};

const char *status_name(numen_status status) {
  switch (status) {
  case NUMEN_OK:
    return "OK";
  case NUMEN_ERR_INVALID_ARGUMENT:
    return "INVALID_ARGUMENT";
  case NUMEN_ERR_ABI_MISMATCH:
    return "ABI_MISMATCH";
  case NUMEN_ERR_EXPRESSION:
    return "EXPRESSION";
  case NUMEN_ERR_INTERNAL:
    return "INTERNAL";
  }
  return "UNKNOWN";
}

Outcome evaluate(Context &context, std::string_view expression, const numen_eval_options &options) {
  numen_result result{};
  result.struct_size = static_cast<uint32_t>(sizeof(numen_result));

  Outcome outcome;
  outcome.status = context.api.fn.evaluate(context.instance, expression.data(), expression.size(), &options,
                                           &result);
  if (result.display_text != nullptr) { outcome.display = result.display_text; }
  if (result.error != nullptr) { outcome.error = result.error; }

  context.api.fn.result_free(&result);
  return outcome;
}

bool is_space(char c) { return std::isspace(static_cast<unsigned char>(c)) != 0; }

std::string_view trim(std::string_view text) {
  std::size_t first = 0;
  std::size_t last = text.size();
  while (first < last && is_space(text[first])) { ++first; }
  while (last > first && is_space(text[last - 1])) { --last; }
  return text.substr(first, last - first);
}

// Mirrors numen/tests/corpus.cpp: a whitespace-delimited "=>" found from the
// right, ignoring any "=>" that is not surrounded by whitespace.
std::size_t find_separator(std::string_view entry) {
  std::size_t pos = entry.rfind("=>");
  while (pos != std::string_view::npos && pos > 0) {
    if (is_space(entry[pos - 1]) && pos + 2 < entry.size() && is_space(entry[pos + 2])) { return pos; }
    pos = pos > 1 ? entry.rfind("=>", pos - 2) : std::string_view::npos;
  }
  return std::string_view::npos;
}

struct Case {
  enum class Expect { Value, Error };

  std::string expression;
  Expect expect = Expect::Value;
  std::string expected;
  int line = 0;
};

struct FileCases {
  std::filesystem::path path;
  std::vector<Case> cases;
};

std::string read_file(const std::filesystem::path &path) {
  std::ifstream file(path, std::ios::binary);
  if (!file) { throw InfrastructureError("cannot open corpus file: " + path.string()); }
  std::string content;
  content.assign(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
  if (file.bad()) { throw InfrastructureError("cannot read corpus file: " + path.string()); }
  return content;
}

FileCases parse_corpus_file(const std::filesystem::path &path) {
  const std::string content = read_file(path);
  FileCases parsed;
  parsed.path = path;

  std::size_t start = 0;
  int line = 0;
  for (;;) {
    const std::size_t end = content.find('\n', start);
    const std::string_view raw(content.data() + start,
                               (end == std::string::npos ? content.size() : end) - start);
    ++line;

    const std::string_view entry = trim(raw);
    if (!entry.empty() && !entry.starts_with('#')) {
      const std::size_t separator = find_separator(entry);
      if (separator == std::string_view::npos) {
        throw InfrastructureError("malformed corpus line " + path.string() + ":" + std::to_string(line));
      }

      Case parsed_case;
      parsed_case.expression = std::string(trim(entry.substr(0, separator)));

      const std::string_view expectation = trim(entry.substr(separator + 2));
      if (expectation.starts_with("!error")) {
        parsed_case.expect = Case::Expect::Error;
        parsed_case.expected = std::string(trim(expectation.substr(6)));
      } else {
        parsed_case.expect = Case::Expect::Value;
        parsed_case.expected = std::string(expectation);
      }

      parsed_case.line = line;
      parsed.cases.push_back(std::move(parsed_case));
    }

    if (end == std::string::npos) { break; }
    start = end + 1;
  }

  return parsed;
}

std::vector<std::filesystem::path> corpus_files() {
  const std::filesystem::path root{NUMEN_C_CORPUS_DIR};
  std::vector<std::filesystem::path> files;

  std::error_code ec;
  if (!std::filesystem::exists(root, ec) || ec) {
    throw InfrastructureError("corpus directory is missing: " + root.string());
  }
  if (!std::filesystem::is_directory(root, ec) || ec) {
    throw InfrastructureError("corpus path is not a directory: " + root.string());
  }

  std::filesystem::directory_iterator iterator(root, ec);
  const std::filesystem::directory_iterator end;
  if (ec) { throw InfrastructureError("cannot enumerate corpus directory: " + root.string()); }
  while (iterator != end) {
    const std::filesystem::directory_entry &entry = *iterator;
    if (entry.path().extension() == ".corpus" && entry.is_regular_file(ec) && !ec) {
      files.push_back(entry.path());
    }
    ec.clear();
    iterator.increment(ec);
    if (ec) { throw InfrastructureError("cannot enumerate corpus directory: " + root.string()); }
  }

  std::sort(files.begin(), files.end());
  if (files.empty()) { throw InfrastructureError("no .corpus files found under: " + root.string()); }
  return files;
}

std::string describe_actual(const Outcome &outcome) {
  if (outcome.status == NUMEN_OK) {
    return "value: \"" + outcome.display + "\"";
  }
  return std::string(status_name(outcome.status)) + " error: \"" + outcome.error + "\"";
}

bool run_case(Context &context, const numen_eval_options &options, const Case &test_case,
              std::string_view source, int line_override) {
  const Outcome outcome = evaluate(context, test_case.expression, options);
  const int line = line_override != 0 ? line_override : test_case.line;

  if (test_case.expect == Case::Expect::Value) {
    if (outcome.status == NUMEN_OK && outcome.display == test_case.expected) { return true; }

    std::fprintf(stderr, "%s:%d: semantic mismatch\n  expr: %s\n  expected: \"%s\"\n  actual: %s\n",
                 std::string(source).c_str(), line, test_case.expression.c_str(),
                 test_case.expected.c_str(), describe_actual(outcome).c_str());
    return false;
  }

  const bool error_expected = outcome.status != NUMEN_OK && !outcome.error.empty();
  const bool message_matches = test_case.expected.empty() || outcome.error == test_case.expected;
  if (error_expected && message_matches) { return true; }

  const std::string expectation =
      test_case.expected.empty() ? "!error" : "!error " + test_case.expected;
  std::fprintf(stderr, "%s:%d: semantic mismatch\n  expr: %s\n  expected: %s\n  actual: %s\n",
               std::string(source).c_str(), line, test_case.expression.c_str(), expectation.c_str(),
               describe_actual(outcome).c_str());
  return false;
}

void clear_rates(Context &context) {
  if (context.api.fn.set_rates(context.instance, nullptr, 0) != NUMEN_OK) {
    throw InfrastructureError("numen_set_rates(NULL, 0) failed");
  }
}

numen_eval_options make_options(Context &context, bool strict) {
  numen_eval_options options{};
  context.api.fn.eval_options_init(&options);
  options.locale = "en_US";
  options.strict = strict ? 1 : 0;
  options.implicit_currency = 1;
  options.implicit_unit = 1;
  options.implicit_timezone = 1;
  options.now_unix_seconds = 1768694400; // 2026-01-18T00:00:00Z
  options.timezone = "Etc/UTC";
  options.relative_dates = 0;
  options.with_timezone = 1;
  options.date_format_neutral = 1;
  options.date_locale = nullptr;
  return options;
}

int run_corpus(Context &context) {
  clear_rates(context);

  const numen_eval_options options = make_options(context, false);
  int mismatches = 0;
  int cases = 0;

  for (const std::filesystem::path &path : corpus_files()) {
    const FileCases parsed = parse_corpus_file(path);
    for (const Case &test_case : parsed.cases) {
      ++cases;
      if (!run_case(context, options, test_case, path.string(), 0)) { ++mismatches; }
    }
  }

  std::printf("corpus: %d cases, %d semantic mismatch(es)\n", cases, mismatches);
  return mismatches;
}

int run_strict(Context &context) {
  clear_rates(context);

  const numen_eval_options options = make_options(context, true);
  const std::vector<Case> cases = {
      {"1+1", Case::Expect::Value, "2", 1},
      {"1 km to m", Case::Expect::Value, "1,000m", 2},
      {"5 323", Case::Expect::Value, "5,323", 3},
      {"today", Case::Expect::Value, "2026-01-18 00:00:00 (Etc/UTC)", 4},
      {"2 hr", Case::Expect::Value, "2 hr", 5},
      {"1<2", Case::Expect::Value, "true", 6},
      {"sqrt(16)", Case::Expect::Value, "4", 7},
      {"foo(1)", Case::Expect::Error, "Unknown function \"foo\"", 8},
      {"(1 + 2", Case::Expect::Error, "Expected ) to close the group", 9},
      {"what time is it currently", Case::Expect::Error, "", 10},
      {"5 bears plus 2 rabbits", Case::Expect::Error, "", 11},
      {"3 in jan", Case::Expect::Error, "", 12},
  };

  int mismatches = 0;
  for (const Case &test_case : cases) {
    if (!run_case(context, options, test_case, "strict", test_case.line)) { ++mismatches; }
  }

  std::printf("strict: %d cases, %d semantic mismatch(es)\n", static_cast<int>(cases.size()), mismatches);
  return mismatches;
}

int run_rates(Context &context) {
  clear_rates(context);
  if (context.api.fn.set_rates(context.instance, numen_test::kCuratedRates.data(),
                               numen_test::kCuratedRates.size()) != NUMEN_OK) {
    throw InfrastructureError("numen_set_rates(curated) failed");
  }

  const numen_eval_options options = make_options(context, false);
  const std::vector<Case> cases = {
      {"100 usd to eur", Case::Expect::Value, "\xE2\x82\xAC" "92.35", 1},
      {"100 usd to gbp", Case::Expect::Value, "\xC2\xA3" "78.91", 2},
      {"100 eur to gbp", Case::Expect::Value, "\xC2\xA3" "85.45", 3},
      {"1 btc to usd", Case::Expect::Value, "$85,470.09", 4},
      {"1 usd to btc", Case::Expect::Value, "\xE2\x82\xBF" "0.0000117", 5},
      {"1 xmr to usd", Case::Expect::Value, "$200", 6},
      {"1 xmr to eur", Case::Expect::Value, "\xE2\x82\xAC" "184.69", 7},
      {"1 shib to usd", Case::Expect::Value, "$0", 8},
      {"1 usd to shib", Case::Expect::Value, "100,000SHIB", 9},
      {"$btc to usd", Case::Expect::Value, "$85,470.09", 10},
      {"$BTC to usd", Case::Expect::Value, "$85,470.09", 11},
      {"100 $xmr to usd", Case::Expect::Value, "$20,000", 12},
      {"100 $usd to eur", Case::Expect::Value, "\xE2\x82\xAC" "92.35", 13},
      {"$100", Case::Expect::Value, "$100", 14},
      {"1 btc to btc", Case::Expect::Value, "\xE2\x82\xBF" "1", 15},
      {"1 m to cm", Case::Expect::Value, "100cm", 16},
  };

  int mismatches = 0;
  for (const Case &test_case : cases) {
    if (!run_case(context, options, test_case, "rates", test_case.line)) { ++mismatches; }
  }

  // A cleared snapshot must fail rather than resurrect the previous rates.
  clear_rates(context);
  const Case stale{"100 usd to eur", Case::Expect::Error, "No conversion rate available for usd.", 17};
  if (!run_case(context, options, stale, "rates", stale.line)) { ++mismatches; }

  std::printf("rates: %d cases, %d semantic mismatch(es)\n", static_cast<int>(cases.size()) + 1, mismatches);
  return mismatches;
}

std::string parse_suite(int argc, char **argv) {
  std::string suite = "all";
  constexpr std::string_view prefix = "--suite=";

  for (int i = 1; i < argc; ++i) {
    const std::string_view argument = argv[i] == nullptr ? std::string_view{} : std::string_view(argv[i]);
    if (argument.starts_with(prefix)) { suite = std::string(argument.substr(prefix.size())); }
  }

  if (suite != "all" && suite != "corpus" && suite != "strict" && suite != "rates") {
    throw InfrastructureError("unknown --suite value: " + suite);
  }
  return suite;
}

} // namespace

int main(int argc, char **argv) {
  try {
    const std::string suite = parse_suite(argc, argv);

    NumenApi api = NumenApi::load(argc, argv);
    numen_instance *instance = api.fn.create();
    if (instance == nullptr) { throw InfrastructureError("numen_create() returned NULL"); }

    Context context{api, instance};
    int mismatches = 0;

    // The curated rates are never active while the corpus oracle runs.
    if (suite == "all" || suite == "corpus") { mismatches += run_corpus(context); }
    if (suite == "all" || suite == "strict") { mismatches += run_strict(context); }
    if (suite == "all" || suite == "rates") { mismatches += run_rates(context); }

    if (mismatches == 0) {
      std::printf("numen_corpus_diff: pass (%s)\n", suite.c_str());
      return 0;
    }

    std::fprintf(stderr, "numen_corpus_diff: %d semantic mismatch(es) (%s)\n", mismatches, suite.c_str());
    return 2;
  } catch (const AbiLoadError &error) {
    std::fprintf(stderr, "INFRASTRUCTURE FAILURE: %s\n", error.what());
    return 3;
  } catch (const InfrastructureError &error) {
    std::fprintf(stderr, "INFRASTRUCTURE FAILURE: %s\n", error.what());
    return 3;
  } catch (const std::exception &error) {
    std::fprintf(stderr, "INFRASTRUCTURE FAILURE: unexpected exception: %s\n", error.what());
    return 3;
  }
}
