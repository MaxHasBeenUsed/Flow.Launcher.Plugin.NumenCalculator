#include "abi_loader.hpp"
#include "mock_provider.hpp"

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {

using numen_test::AbiLoadError;
using numen_test::NumenApi;

NumenApi *g_api = nullptr;
numen_instance *g_instance = nullptr;
int g_checks = 0;
int g_failures = 0;

void check_impl(bool condition, std::string_view what, int line) {
  ++g_checks;
  if (!condition) {
    ++g_failures;
    std::fprintf(stderr, "FAIL %s:%d: %.*s\n", __FILE__, line, static_cast<int>(what.size()), what.data());
  }
}

template <typename Actual, typename Expected>
void check_equal(const Actual &actual, const Expected &expected, const char *what, int line) {
  ++g_checks;
  if (!(actual == expected)) {
    ++g_failures;
    std::ostringstream message;
    message << "FAIL " << __FILE__ << ":" << line << ": " << what << ": expected " << expected << ", got "
            << actual;
    std::fprintf(stderr, "%s\n", message.str().c_str());
  }
}

void check_close(double actual, double expected, double tolerance, const char *what, int line) {
  ++g_checks;
  if (!(std::abs(actual - expected) <= tolerance)) {
    ++g_failures;
    std::ostringstream message;
    message.precision(17);
    message << "FAIL " << __FILE__ << ":" << line << ": " << what << ": expected " << expected
            << " +/- " << tolerance << ", got " << actual;
    std::fprintf(stderr, "%s\n", message.str().c_str());
  }
}

void check_cstr(const char *actual, std::string_view expected, const char *what, int line) {
  ++g_checks;
  if (actual == nullptr || std::string_view(actual) != expected) {
    ++g_failures;
    std::fprintf(stderr, "FAIL %s:%d: %s: expected \"%.*s\", got ", __FILE__, line, what,
                 static_cast<int>(expected.size()), expected.data());
    if (actual == nullptr) {
      std::fprintf(stderr, "NULL\n");
    } else {
      std::fprintf(stderr, "\"%s\"\n", actual);
    }
  }
}

void check_prefix(const char *actual, std::string_view prefix, const char *what, int line) {
  ++g_checks;
  if (actual == nullptr || std::string_view(actual).substr(0, prefix.size()) != prefix) {
    ++g_failures;
    std::fprintf(stderr, "FAIL %s:%d: %s: expected prefix \"%.*s\", got %s\n", __FILE__, line, what,
                 static_cast<int>(prefix.size()), prefix.data(), actual == nullptr ? "NULL" : actual);
  }
}

void check_null(const void *actual, const char *what, int line) {
  ++g_checks;
  if (actual != nullptr) {
    ++g_failures;
    std::fprintf(stderr, "FAIL %s:%d: %s: expected NULL, got %p\n", __FILE__, line, what,
                 const_cast<void *>(actual));
  }
}

#define CHECK(condition) ::check_impl((condition), #condition, __LINE__)
#define CHECK_EQ(actual, expected) ::check_equal((actual), (expected), #actual, __LINE__)
#define CHECK_CLOSE(actual, expected, tolerance) ::check_close((actual), (expected), (tolerance), #actual, __LINE__)
#define CHECK_STR(actual, expected) ::check_cstr((actual), (expected), #actual, __LINE__)
#define CHECK_PREFIX(actual, expected) ::check_prefix((actual), (expected), #actual, __LINE__)
#define CHECK_NULL(actual) ::check_null((actual), #actual, __LINE__)

struct Result {
  numen_result value{};
  bool freed = false;

  Result() { value.struct_size = static_cast<uint32_t>(sizeof(numen_result)); }
  Result(const Result &) = delete;
  Result &operator=(const Result &) = delete;
  ~Result() { release(); }

  numen_result *out() { return &value; }

  void release() {
    if (!freed) {
      g_api->fn.result_free(&value);
      freed = true;
    }
  }
};

numen_eval_options initialized_options() {
  numen_eval_options options{};
  g_api->fn.eval_options_init(&options);
  return options;
}

// Deterministic frame shared by the expression checks: 2026-01-18T00:00:00Z,
// neutral dates, no relative elision.
numen_eval_options frozen_options(bool strict = false) {
  numen_eval_options options = initialized_options();
  options.locale = "en_US";
  options.strict = strict ? 1 : 0;
  options.timezone = "Etc/UTC";
  options.now_unix_seconds = 1768694400;
  options.relative_dates = 0;
  options.with_timezone = 1;
  options.date_format_neutral = 1;
  options.date_locale = nullptr;
  return options;
}

numen_status evaluate(std::string_view expr, const numen_eval_options &options, Result &result) {
  result.value.struct_size = static_cast<uint32_t>(sizeof(numen_result));
  return g_api->fn.evaluate(g_instance, expr.data(), expr.size(), &options, result.out());
}

void expect_success(std::string_view expr, const numen_eval_options &options, Result &result) {
  CHECK_EQ(evaluate(expr, options, result), NUMEN_OK);
  CHECK_NULL(result.value.error);
  CHECK(result.value.display_text != nullptr);
}

void test_versions() {
  CHECK_EQ(g_api->fn.abi_version(), NUMEN_C_ABI_VERSION);
  const char *engine = g_api->fn.engine_version();
  CHECK(engine != nullptr);
  CHECK_STR(engine, "0.8.0");
}

void test_struct_sizes() {
  CHECK_EQ(sizeof(numen_eval_options), static_cast<size_t>(72));
  CHECK_EQ(sizeof(numen_result), static_cast<size_t>(248));
}

void test_options_init() {
  numen_eval_options options;
  std::memset(&options, 0xAB, sizeof(options));
  g_api->fn.eval_options_init(&options);

  CHECK_EQ(options.struct_size, static_cast<uint32_t>(sizeof(numen_eval_options)));
  CHECK_NULL(options.locale);
  CHECK_EQ(options.strict, 0);
  CHECK_EQ(options.implicit_currency, 1);
  CHECK_EQ(options.implicit_unit, 1);
  CHECK_EQ(options.implicit_timezone, 1);
  CHECK_EQ(options.now_unix_seconds, static_cast<int64_t>(0));
  CHECK_NULL(options.timezone);
  CHECK_EQ(options.relative_dates, 1);
  CHECK_EQ(options.with_timezone, 1);
  CHECK_EQ(options.date_format_neutral, 0);
  CHECK_NULL(options.date_locale);

  g_api->fn.eval_options_init(nullptr);
}

void test_round_trips() {
  const numen_eval_options options = frozen_options();

  {
    Result result;
    expect_success("1+1", options, result);
    CHECK_STR(result.value.display_text, "2");
    CHECK_EQ(result.value.kind, NUMEN_VALUE_NUMBER);
    CHECK_CLOSE(result.value.number_value, 2.0, 0.0);
    CHECK_STR(result.value.number_text, "2");
    CHECK_EQ(result.value.number_format, NUMEN_FORMAT_DECIMAL);
    CHECK_EQ(result.value.is_percentage, 0);
    CHECK_EQ(result.value.has_unit, 0);
    CHECK_NULL(result.value.unit_raw);
    CHECK_NULL(result.value.unit_render);
    CHECK_NULL(result.value.unit_name);
    CHECK_EQ(result.value.unit_decimals, -1);
    CHECK_EQ(result.value.unit_symbol_prefix, 0);
    CHECK_EQ(result.value.has_conversion, 0);
    CHECK_EQ(result.value.boolean_value, 0);
    CHECK_NULL(result.value.text_value);
  }

  {
    Result result;
    expect_success("1 km to m", options, result);
    CHECK_STR(result.value.display_text, "1,000m");
    CHECK_EQ(result.value.kind, NUMEN_VALUE_NUMBER);
    CHECK_CLOSE(result.value.number_value, 1000.0, 0.0);
    CHECK_STR(result.value.number_text, "1,000");
    CHECK_EQ(result.value.number_format, NUMEN_FORMAT_DECIMAL);
    CHECK_EQ(result.value.is_percentage, 0);
    CHECK_EQ(result.value.has_unit, 1);
    CHECK_STR(result.value.unit_raw, "m");
    CHECK_STR(result.value.unit_render, "m");
    CHECK_STR(result.value.unit_name, "meter");
    CHECK_EQ(result.value.unit_decimals, -1);
    CHECK_EQ(result.value.unit_symbol_prefix, 0);
    CHECK_EQ(result.value.has_conversion, 1);
    CHECK_EQ(result.value.conversion.kind, NUMEN_CONVERSION_UNIT);
    CHECK_EQ(result.value.conversion.implicit, 0);
    CHECK_EQ(result.value.conversion.has_from, 1);
    CHECK_STR(result.value.conversion.from_text, "km");
    CHECK_STR(result.value.conversion.to_text, "m");
    CHECK_EQ(result.value.conversion.from_offset_seconds, 0);
    CHECK_EQ(result.value.conversion.to_offset_seconds, 0);
  }

  {
    Result result;
    expect_success("100 usd + 20 usd", options, result);
    CHECK_STR(result.value.display_text, "$120");
    CHECK_EQ(result.value.kind, NUMEN_VALUE_NUMBER);
    CHECK_CLOSE(result.value.number_value, 120.0, 0.0);
    CHECK_STR(result.value.number_text, "120");
    CHECK_EQ(result.value.has_unit, 1);
    CHECK_STR(result.value.unit_raw, "usd");
    CHECK_STR(result.value.unit_render, "$");
    CHECK_STR(result.value.unit_name, "US Dollar");
    CHECK_EQ(result.value.unit_decimals, 2);
    CHECK_EQ(result.value.unit_symbol_prefix, 1);
    CHECK_EQ(result.value.has_conversion, 0);
  }

  {
    Result result;
    expect_success("today", options, result);
    CHECK_STR(result.value.display_text, "2026-01-18 00:00:00 (Etc/UTC)");
    CHECK_EQ(result.value.kind, NUMEN_VALUE_DATETIME);
    CHECK_EQ(result.value.unix_seconds, static_cast<int64_t>(1768694400));
    CHECK_EQ(result.value.nanos, 0);
    CHECK_EQ(result.value.offset_seconds, 0);
    CHECK_STR(result.value.timezone_name, "Etc/UTC");
    CHECK_STR(result.value.rfc3339, "2026-01-18T00:00:00Z");
    CHECK_STR(result.value.timezone_string, "Etc/UTC");
    CHECK_EQ(result.value.has_conversion, 0);
  }

  {
    Result result;
    expect_success("2 hr", options, result);
    CHECK_STR(result.value.display_text, "2 hr");
    CHECK_EQ(result.value.kind, NUMEN_VALUE_DURATION);
    CHECK_EQ(result.value.years, static_cast<int64_t>(0));
    CHECK_EQ(result.value.months, static_cast<int64_t>(0));
    CHECK_EQ(result.value.seconds, static_cast<int64_t>(7200));
    CHECK_EQ(result.value.subsecond_nanos, static_cast<int64_t>(0));
    // durationFrom always materializes seconds and subsecond, even when the
    // subsecond remainder is zero, so bits 2 and 3 are present.
    CHECK_EQ(result.value.present_mask, static_cast<uint32_t>(12));
    CHECK_CLOSE(result.value.total_seconds, 7200.0, 0.0);
  }

  {
    Result result;
    expect_success("1<2", options, result);
    CHECK_STR(result.value.display_text, "true");
    CHECK_EQ(result.value.kind, NUMEN_VALUE_BOOLEAN);
    CHECK_EQ(result.value.boolean_value, 1);
    CHECK_EQ(result.value.has_conversion, 0);
  }

  {
    Result result;
    expect_success("\"text\"", options, result);
    CHECK_STR(result.value.display_text, "text");
    CHECK_EQ(result.value.kind, NUMEN_VALUE_TEXT);
    CHECK_STR(result.value.text_value, "text");
    CHECK_EQ(result.value.has_conversion, 0);
  }
}

void test_utf8_expression_and_result() {
  CHECK_EQ(g_api->fn.set_rates(g_instance, numen_test::kAbiRates.data(), numen_test::kAbiRates.size()),
           NUMEN_OK);

  const numen_eval_options options = frozen_options();
  Result result;
  expect_success("1 \xE2\x82\xAC to usd", options, result);
  CHECK_STR(result.value.display_text, "$1.11");
  CHECK_EQ(result.value.kind, NUMEN_VALUE_NUMBER);
  CHECK_CLOSE(result.value.number_value, 1.0 / 0.9, 1e-12);
  CHECK_STR(result.value.number_text, "1.11");
  CHECK_STR(result.value.unit_raw, "usd");
  CHECK_STR(result.value.unit_render, "$");
  CHECK_STR(result.value.unit_name, "US Dollar");
  CHECK_EQ(result.value.unit_decimals, 2);
  CHECK_EQ(result.value.unit_symbol_prefix, 1);
  CHECK_EQ(result.value.has_conversion, 1);
  CHECK_EQ(result.value.conversion.kind, NUMEN_CONVERSION_UNIT);
  CHECK_STR(result.value.conversion.from_text, "\xE2\x82\xAC");
  CHECK_STR(result.value.conversion.to_text, "$");
  CHECK_EQ(result.value.conversion.has_from, 1);

  CHECK_EQ(g_api->fn.set_rates(g_instance, nullptr, 0), NUMEN_OK);
}

void test_long_text() {
  const std::string payload(32 * 1024, 'a');
  const std::string expression = "\"" + payload + "\"";

  const numen_eval_options options = frozen_options();
  Result result;
  expect_success(expression, options, result);
  CHECK_EQ(result.value.kind, NUMEN_VALUE_TEXT);
  CHECK(result.value.display_text != nullptr);
  CHECK(result.value.text_value != nullptr);
  if (result.value.display_text != nullptr) {
    CHECK_EQ(std::string_view(result.value.display_text).size(), payload.size());
    CHECK(payload == result.value.display_text);
  }
  if (result.value.text_value != nullptr) {
    CHECK_EQ(std::string_view(result.value.text_value).size(), payload.size());
    CHECK(payload == result.value.text_value);
  }

  result.release();
  CHECK_NULL(result.value.display_text);
  CHECK_NULL(result.value.text_value);
  CHECK_EQ(result.value.struct_size, static_cast<uint32_t>(0));
}

void test_errors() {
  const numen_eval_options options = frozen_options();

  {
    Result result;
    CHECK_EQ(evaluate("foo(1)", options, result), NUMEN_ERR_EXPRESSION);
    CHECK(result.value.error != nullptr && result.value.error[0] != '\0');
    CHECK_STR(result.value.error, "Unknown function \"foo\"");
    CHECK_NULL(result.value.display_text);
  }

  {
    numen_eval_options invalid_timezone = options;
    invalid_timezone.timezone = "Mars/Olympus_Mons";
    Result result;
    CHECK_EQ(evaluate("1+1", invalid_timezone, result), NUMEN_ERR_INVALID_ARGUMENT);
    CHECK(result.value.error != nullptr && result.value.error[0] != '\0');
    CHECK_PREFIX(result.value.error, "Unknown timezone: Mars/Olympus_Mons");
    CHECK_NULL(result.value.display_text);
  }

  {
    const std::string invalid_utf8 = "\xC3\x28";
    Result result;
    CHECK_EQ(evaluate(invalid_utf8, options, result), NUMEN_ERR_INVALID_ARGUMENT);
    CHECK(result.value.error != nullptr && result.value.error[0] != '\0');
    CHECK_NULL(result.value.display_text);
  }
}

void test_invalid_arguments() {
  const numen_eval_options options = frozen_options();

  {
    Result result;
    CHECK_EQ(g_api->fn.evaluate(g_instance, "1+1", 3, nullptr, result.out()), NUMEN_ERR_INVALID_ARGUMENT);
    CHECK_NULL(result.value.display_text);
  }

  {
    Result result;
    CHECK_EQ(g_api->fn.evaluate(g_instance, "1+1", 3, &options, nullptr), NUMEN_ERR_INVALID_ARGUMENT);
  }

  {
    Result result;
    CHECK_EQ(g_api->fn.evaluate(nullptr, "1+1", 3, &options, result.out()), NUMEN_ERR_INVALID_ARGUMENT);
    CHECK_NULL(result.value.display_text);
  }

  {
    Result result;
    CHECK_EQ(g_api->fn.evaluate(g_instance, nullptr, 1, &options, result.out()), NUMEN_ERR_INVALID_ARGUMENT);
    CHECK_NULL(result.value.display_text);
  }

  {
    numen_eval_options bad_options = options;
    bad_options.struct_size = static_cast<uint32_t>(sizeof(numen_eval_options) - 1);
    Result result;
    CHECK_EQ(evaluate("1+1", bad_options, result), NUMEN_ERR_ABI_MISMATCH);
  }

  {
    Result result;
    result.value.struct_size = static_cast<uint32_t>(sizeof(numen_result) - 1);
    CHECK_EQ(g_api->fn.evaluate(g_instance, "1+1", 3, &options, result.out()), NUMEN_ERR_ABI_MISMATCH);
    result.value.struct_size = static_cast<uint32_t>(sizeof(numen_result));
  }

  {
    Result result;
    CHECK_EQ(evaluate("", options, result), NUMEN_ERR_EXPRESSION);
    CHECK(result.value.error != nullptr && result.value.error[0] != '\0');
    CHECK_NULL(result.value.display_text);
  }

  {
    numen_eval_options bad_neutral = options;
    bad_neutral.date_format_neutral = 2;
    Result result;
    CHECK_EQ(evaluate("today", bad_neutral, result), NUMEN_ERR_INVALID_ARGUMENT);
    CHECK(result.value.error != nullptr && result.value.error[0] != '\0');
  }
}

void test_rate_injection() {
  const numen_eval_options options = frozen_options();
  CHECK_EQ(g_api->fn.set_rates(g_instance, numen_test::kAbiRates.data(), numen_test::kAbiRates.size()),
           NUMEN_OK);

  {
    Result result;
    expect_success("100 usd to eur", options, result);
    CHECK_STR(result.value.display_text, "\xE2\x82\xAC" "90");
    CHECK_CLOSE(result.value.number_value, 90.0, 1e-12);
    CHECK_STR(result.value.number_text, "90");
    CHECK_STR(result.value.unit_raw, "eur");
    CHECK_STR(result.value.unit_render, "\xE2\x82\xAC");
    CHECK_STR(result.value.unit_name, "Euro");
    CHECK_EQ(result.value.unit_decimals, 2);
    CHECK_EQ(result.value.unit_symbol_prefix, 1);
    CHECK_EQ(result.value.has_conversion, 1);
    CHECK_EQ(result.value.conversion.kind, NUMEN_CONVERSION_UNIT);
    CHECK_EQ(result.value.conversion.implicit, 0);
    CHECK_STR(result.value.conversion.from_text, "usd");
    CHECK_STR(result.value.conversion.to_text, "\xE2\x82\xAC");
  }

  CHECK_EQ(g_api->fn.set_rates(g_instance, nullptr, 0), NUMEN_OK);

  {
    Result result;
    CHECK_EQ(evaluate("100 usd to eur", options, result), NUMEN_ERR_EXPRESSION);
    CHECK_STR(result.value.error, "No conversion rate available for usd.");
    CHECK_NULL(result.value.display_text);
  }
}

void test_result_free() {
  const numen_eval_options options = frozen_options();

  Result result;
  expect_success("1+1", options, result);
  g_api->fn.result_free(result.out());
  result.freed = true;
  CHECK_NULL(result.value.display_text);
  CHECK_NULL(result.value.error);
  CHECK_EQ(result.value.struct_size, static_cast<uint32_t>(0));

  // Idempotent for an already-freed struct.
  g_api->fn.result_free(result.out());

  numen_result zeroed{};
  g_api->fn.result_free(&zeroed);
  g_api->fn.result_free(nullptr);
  g_api->fn.destroy(nullptr);
}

void test_concurrent_evaluate_and_rates() {
  constexpr int kThreads = 8;
  constexpr int kIterations = 250; // 2,000 evaluations in total

  numen_rate rates_a[] = {{"usd", 1.0}, {"eur", 0.9}};
  numen_rate rates_b[] = {{"usd", 1.0}, {"eur", 0.8}};

  CHECK_EQ(g_api->fn.set_rates(g_instance, rates_a, 2), NUMEN_OK);

  const numen_eval_options options = frozen_options();
  constexpr std::string_view expression = "100 usd to eur";

  std::mutex evaluate_mutex;
  std::atomic<int> evaluate_failures{0};
  std::atomic<int> rate_failures{0};
  std::atomic<bool> stop{false};

  // libnumen is not documented as thread-safe and the plugin serializes
  // evaluations; set_rates is allowed to run concurrently and must never
  // leak a different snapshot into one evaluation.
  std::thread rate_thread([&] {
    for (int i = 0; !stop.load(std::memory_order_relaxed); ++i) {
      numen_rate *rates = (i % 2) == 0 ? rates_a : rates_b;
      if (g_api->fn.set_rates(g_instance, rates, 2) != NUMEN_OK) {
        rate_failures.fetch_add(1, std::memory_order_relaxed);
      }
    }
  });

  std::vector<std::thread> workers;
  workers.reserve(kThreads);
  for (int thread_index = 0; thread_index < kThreads; ++thread_index) {
    workers.emplace_back([&] {
      for (int i = 0; i < kIterations; ++i) {
        numen_result result{};
        result.struct_size = static_cast<uint32_t>(sizeof(numen_result));

        numen_status status;
        {
          std::lock_guard<std::mutex> lock(evaluate_mutex);
          status = g_api->fn.evaluate(g_instance, expression.data(), expression.size(), &options, &result);
        }

        const std::string_view display =
            result.display_text == nullptr ? std::string_view{} : std::string_view(result.display_text);
        const bool ok = status == NUMEN_OK && result.error == nullptr &&
                        (display == "\xE2\x82\xAC" "90" || display == "\xE2\x82\xAC" "80");
        if (!ok) {
          evaluate_failures.fetch_add(1, std::memory_order_relaxed);
        }

        g_api->fn.result_free(&result);
      }
    });
  }

  for (std::thread &worker : workers) {
    worker.join();
  }
  stop.store(true, std::memory_order_relaxed);
  rate_thread.join();

  CHECK_EQ(rate_failures.load(), 0);
  CHECK_EQ(evaluate_failures.load(), 0);
  CHECK_EQ(g_api->fn.set_rates(g_instance, nullptr, 0), NUMEN_OK);
}

} // namespace

int main(int argc, char **argv) {
  NumenApi api;
  try {
    api = NumenApi::load(argc, argv);
  } catch (const AbiLoadError &error) {
    std::fprintf(stderr, "INFRASTRUCTURE FAILURE: %s\n", error.what());
    return 3;
  } catch (const std::exception &error) {
    std::fprintf(stderr, "INFRASTRUCTURE FAILURE: %s\n", error.what());
    return 3;
  }

  g_api = &api;
  g_instance = api.fn.create();
  if (g_instance == nullptr) {
    std::fprintf(stderr, "INFRASTRUCTURE FAILURE: numen_create() returned NULL\n");
    return 3;
  }

  test_versions();
  test_struct_sizes();
  test_options_init();
  test_round_trips();
  test_utf8_expression_and_result();
  test_long_text();
  test_errors();
  test_invalid_arguments();
  test_rate_injection();
  test_result_free();
  test_concurrent_evaluate_and_rates();

  api.fn.destroy(g_instance);
  g_instance = nullptr;
  g_api = nullptr;

  if (g_failures == 0) {
    std::printf("numen_abi_tests: %d checks passed\n", g_checks);
    return 0;
  }

  std::fprintf(stderr, "numen_abi_tests: %d of %d checks failed\n", g_failures, g_checks);
  return 1;
}
