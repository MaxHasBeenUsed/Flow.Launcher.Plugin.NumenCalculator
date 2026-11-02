#include "numen_c.h"

#include <numen/numen.hpp>
#include <numen/tz.hpp>

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <memory>
#include <new>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <variant>

class RateProvider final : public numen::AbstractCurrencyProvider {
public:
  std::optional<numen::ExchangeRate> getRate(std::string_view code) const override;
  void updateRates() override {}

  void setRates(std::unordered_map<std::string, double> rates);
  void pin();
  void unpin();

private:
  std::atomic<std::shared_ptr<const std::unordered_map<std::string, double>>> rates_{
      std::make_shared<const std::unordered_map<std::string, double>>()};
  static thread_local std::shared_ptr<const std::unordered_map<std::string, double>> t_pinned_;
};

thread_local std::shared_ptr<const std::unordered_map<std::string, double>> RateProvider::t_pinned_;

struct numen_instance {
  std::unique_ptr<numen::Numen> engine;
  RateProvider *rateProvider = nullptr;
};

namespace {

std::optional<numen::ExchangeRate> rateFromMap(const std::unordered_map<std::string, double> &rates,
                                               const std::string &key) {
  if (auto it = rates.find(key); it != rates.end()) {
    return numen::ExchangeRate{.rate = it->second, .lastUpdatedAt = std::nullopt};
  }
  return std::nullopt;
}

bool isValidUtf8(std::string_view text) {
  const auto *bytes = reinterpret_cast<const unsigned char *>(text.data());
  const std::size_t size = text.size();
  std::size_t i = 0;

  while (i < size) {
    const unsigned char first = bytes[i];
    if (first < 0x80) {
      ++i;
      continue;
    }

    std::size_t length = 0;
    std::uint32_t codepoint = 0;
    if ((first & 0xE0) == 0xC0) {
      length = 2;
      codepoint = static_cast<std::uint32_t>(first & 0x1F);
    } else if ((first & 0xF0) == 0xE0) {
      length = 3;
      codepoint = static_cast<std::uint32_t>(first & 0x0F);
    } else if ((first & 0xF8) == 0xF0) {
      length = 4;
      codepoint = static_cast<std::uint32_t>(first & 0x07);
    } else {
      return false;
    }

    if (i + length > size) return false;
    for (std::size_t offset = 1; offset < length; ++offset) {
      const unsigned char next = bytes[i + offset];
      if ((next & 0xC0) != 0x80) return false;
      codepoint = (codepoint << 6) | static_cast<std::uint32_t>(next & 0x3F);
    }

    if (length == 2u && codepoint < 0x80u) return false;
    if (length == 3u && codepoint < 0x800u) return false;
    if (length == 4u && codepoint < 0x10000u) return false;
    if (codepoint > 0x10FFFFu) return false;
    if (codepoint >= 0xD800u && codepoint <= 0xDFFFu) return false;

    i += length;
  }

  return true;
}

void asciiLowerInPlace(std::string &text) {
  for (char &c : text) {
    if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
  }
}

std::string normaliseRateCode(std::string_view code) {
  std::string normalised(code);
  asciiLowerInPlace(normalised);
  if (!normalised.empty() && normalised.front() == '$') normalised.erase(0, 1);
  return normalised;
}

char *duplicateString(std::string_view text) {
  char *buffer = static_cast<char *>(std::malloc(text.size() + 1));
  if (!buffer) throw std::bad_alloc();
  if (!text.empty()) std::memcpy(buffer, text.data(), text.size());
  buffer[text.size()] = '\0';
  return buffer;
}

void freeResultFields(numen_result *result) {
  std::free(result->error);
  std::free(result->display_text);
  std::free(result->number_text);
  std::free(result->unit_raw);
  std::free(result->unit_render);
  std::free(result->unit_name);
  std::free(result->timezone_name);
  std::free(result->rfc3339);
  std::free(result->timezone_string);
  std::free(result->text_value);
  std::free(result->conversion.from_text);
  std::free(result->conversion.to_text);
}

void releaseResultFields(numen_result *result) {
  freeResultFields(result);
  const std::uint32_t structSize = result->struct_size;
  std::memset(result, 0, sizeof(*result));
  result->struct_size = structSize;
}

numen_status failResult(numen_result *result, numen_status status, std::string_view message) {
  result->status = status;
  result->error = duplicateString(message);
  return status;
}

void setInternalError(numen_result *result) {
  result->status = NUMEN_ERR_INTERNAL;
  try {
    result->error = duplicateString("Internal error");
  } catch (...) {
    result->error = nullptr;
  }
}

class RatePin {
public:
  explicit RatePin(RateProvider &provider) : m_provider(provider) { m_provider.pin(); }
  ~RatePin() { m_provider.unpin(); }

  RatePin(const RatePin &) = delete;
  RatePin &operator=(const RatePin &) = delete;

private:
  RateProvider &m_provider;
};

void fillNumber(const numen::Number &number, numen_result *out) {
  out->kind = NUMEN_VALUE_NUMBER;
  out->number_value = number.n;
  out->number_text = duplicateString(number.text);
  out->number_format = static_cast<std::int32_t>(number.format);
  out->is_percentage = number.isPercentage ? 1 : 0;
  out->has_unit = number.unit.has_value() ? 1 : 0;
  out->unit_decimals = -1;

  if (!number.unit) return;

  const numen::Number::Unit &unit = *number.unit;
  out->unit_raw = duplicateString(unit.raw);
  if (!unit.resolved) return;

  const CompoundUnit &resolved = *unit.resolved;
  out->unit_render = duplicateString(resolved.render());

  const std::string name = resolved.name();
  if (!name.empty()) out->unit_name = duplicateString(name);

  if (const UnitDef *sole = resolved.sole()) {
    out->unit_decimals = sole->decimals.value_or(-1);
    out->unit_symbol_prefix = sole->symbolPrefix ? 1 : 0;
  }
}

void fillDateTime(const numen::DateTime &dateTime, numen_result *out) {
  out->kind = NUMEN_VALUE_DATETIME;

  const auto wholeSeconds = std::chrono::floor<std::chrono::seconds>(dateTime.time);
  out->unix_seconds = static_cast<std::int64_t>(wholeSeconds.time_since_epoch().count());
  out->nanos = static_cast<std::int32_t>(
      std::chrono::duration_cast<std::chrono::nanoseconds>(dateTime.time - wholeSeconds).count());
  out->offset_seconds = static_cast<std::int32_t>(dateTime.offset.count());

  if (dateTime.tz) {
    out->timezone_name = duplicateString(dateTime.tz->name());
    out->timezone_string = duplicateString(dateTime.toTimezoneString());
  }

  out->rfc3339 = duplicateString(dateTime.toRFC3339());
}

void fillDuration(const numen::Duration &duration, numen_result *out) {
  out->kind = NUMEN_VALUE_DURATION;

  if (duration.years) {
    out->years = duration.years->count();
    out->present_mask |= 1u;
  }
  if (duration.months) {
    out->months = duration.months->count();
    out->present_mask |= 2u;
  }
  if (duration.seconds) {
    out->seconds = duration.seconds->count();
    out->present_mask |= 4u;
  }
  if (duration.subsecond) {
    out->subsecond_nanos = duration.subsecond->count();
    out->present_mask |= 8u;
  }

  out->total_seconds = static_cast<double>(duration.total().count());
}

void fillValue(const numen::ValueType &value, numen_result *out) {
  if (const auto *number = std::get_if<numen::Number>(&value)) {
    fillNumber(*number, out);
  } else if (const auto *dateTime = std::get_if<numen::DateTime>(&value)) {
    fillDateTime(*dateTime, out);
  } else if (const auto *duration = std::get_if<numen::Duration>(&value)) {
    fillDuration(*duration, out);
  } else if (const auto *boolean = std::get_if<numen::Boolean>(&value)) {
    out->kind = NUMEN_VALUE_BOOLEAN;
    out->boolean_value = boolean->value ? 1 : 0;
  } else if (const auto *text = std::get_if<std::string>(&value)) {
    out->kind = NUMEN_VALUE_TEXT;
    out->text_value = duplicateString(*text);
  }
}

void fillConversion(const numen::Conversion &conversion, numen_result *out) {
  out->has_conversion = 1;
  out->conversion.implicit = conversion.implicit ? 1 : 0;

  if (const auto *unit = conversion.as<numen::Number::Unit>()) {
    out->conversion.kind = NUMEN_CONVERSION_UNIT;
    out->conversion.has_from = unit->from.has_value() ? 1 : 0;
    if (unit->from) out->conversion.from_text = duplicateString(unit->from->raw);
    out->conversion.to_text =
        duplicateString(unit->to.resolved ? unit->to.resolved->render() : unit->to.raw);
    return;
  }

  if (const auto *timezone = conversion.as<numen::Timezone>()) {
    out->conversion.kind = NUMEN_CONVERSION_TIMEZONE;
    out->conversion.has_from = timezone->from.has_value() ? 1 : 0;
    if (timezone->from) {
      out->conversion.from_text = duplicateString(timezone->from->toString());
      out->conversion.from_offset_seconds = static_cast<std::int32_t>(timezone->from->offset.count());
    }
    out->conversion.to_text = duplicateString(timezone->to.toString());
    out->conversion.to_offset_seconds = static_cast<std::int32_t>(timezone->to.offset.count());
    return;
  }

  if (const auto *text = conversion.as<std::string>()) {
    out->conversion.kind = NUMEN_CONVERSION_CONVERTER;
    out->conversion.has_from = text->from.has_value() ? 1 : 0;
    if (text->from) out->conversion.from_text = duplicateString(*text->from);
    out->conversion.to_text = duplicateString(text->to);
    return;
  }

  out->conversion.kind = NUMEN_CONVERSION_NONE;
}

numen_status evaluateInto(numen_instance *instance, const char *expr, std::size_t expr_len,
                          const numen_eval_options *options, numen_result *out) {
  if (!instance) return failResult(out, NUMEN_ERR_INVALID_ARGUMENT, "Instance is null");

  [[maybe_unused]] RatePin pin(*instance->rateProvider);

  if (!options) return failResult(out, NUMEN_ERR_INVALID_ARGUMENT, "Options are null");
  if (options->struct_size != static_cast<std::uint32_t>(sizeof(numen_eval_options))) {
    return failResult(out, NUMEN_ERR_ABI_MISMATCH, "Options struct size mismatch");
  }
  if (expr_len == 0) return failResult(out, NUMEN_ERR_EXPRESSION, "Expression is empty");
  if (!expr) return failResult(out, NUMEN_ERR_INVALID_ARGUMENT, "Expression is null");

  const std::string_view expression(expr, expr_len);
  if (!isValidUtf8(expression)) {
    return failResult(out, NUMEN_ERR_INVALID_ARGUMENT, "Expression is not valid UTF-8");
  }
  if (options->locale && !isValidUtf8(std::string_view(options->locale))) {
    return failResult(out, NUMEN_ERR_INVALID_ARGUMENT, "Locale is not valid UTF-8");
  }
  if (options->timezone && !isValidUtf8(std::string_view(options->timezone))) {
    return failResult(out, NUMEN_ERR_INVALID_ARGUMENT, "Timezone is not valid UTF-8");
  }
  if (options->date_locale && !isValidUtf8(std::string_view(options->date_locale))) {
    return failResult(out, NUMEN_ERR_INVALID_ARGUMENT, "Date locale is not valid UTF-8");
  }
  if (options->date_format_neutral != 0 && options->date_format_neutral != 1) {
    return failResult(out, NUMEN_ERR_INVALID_ARGUMENT, "date_format_neutral must be 0 or 1");
  }

  const numen::tz::time_zone *zone = nullptr;
  if (options->timezone && *options->timezone) {
    try {
      zone = numen::tz::locate_zone(options->timezone);
    } catch (...) {
      zone = nullptr;
    }
    if (!zone) {
      return failResult(out, NUMEN_ERR_INVALID_ARGUMENT,
                        std::string("Unknown timezone: ").append(options->timezone));
    }
  }

  numen::EvalOptions eval{};
  eval.parseOptions.strict = options->strict != 0;
  if (options->locale && *options->locale) eval.parseOptions.locale = std::string(options->locale);

  if (options->now_unix_seconds != 0) {
    eval.now = numen::TimePoint{std::chrono::seconds{options->now_unix_seconds}};
  }
  eval.timezone = zone;

  eval.implicitCurrencyConversion = options->implicit_currency != 0;
  eval.implicitUnitConversion = options->implicit_unit != 0;
  eval.implicitTimezoneConversion = options->implicit_timezone != 0;

  eval.dateTimeFormat.relative = options->relative_dates != 0;
  eval.dateTimeFormat.withTz = options->with_timezone != 0;
  eval.dateTimeFormat.format = options->date_format_neutral != 0
                                   ? numen::DateTimeFormatOptions::TimeFormat::Neutral
                                   : numen::DateTimeFormatOptions::TimeFormat::Local;
  if (options->date_locale && *options->date_locale) {
    eval.dateTimeFormat.locale = std::string(options->date_locale);
  }

  const auto computed = instance->engine->compute(expression, eval);
  if (!computed) return failResult(out, NUMEN_ERR_EXPRESSION, computed.error());

  const numen::DateTimeFormatOptions formatOptions = eval.effectiveDateTimeFormat();
  out->display_text = duplicateString(computed->toString(formatOptions));
  fillValue(computed->value, out);
  if (computed->conversion) fillConversion(*computed->conversion, out);

  out->status = NUMEN_OK;
  return NUMEN_OK;
}

} // namespace

std::optional<numen::ExchangeRate> RateProvider::getRate(std::string_view code) const {
  std::shared_ptr<const std::unordered_map<std::string, double>> snapshot = t_pinned_;
  if (!snapshot) snapshot = rates_.load();
  if (!snapshot) return std::nullopt;

  std::string key(code);
  asciiLowerInPlace(key);

  if (auto rate = rateFromMap(*snapshot, key)) return rate;

  std::string stripped = normaliseRateCode(code);
  if (stripped != key) return rateFromMap(*snapshot, stripped);

  return std::nullopt;
}

void RateProvider::setRates(std::unordered_map<std::string, double> rates) {
  rates_.store(std::make_shared<const std::unordered_map<std::string, double>>(std::move(rates)));
}

void RateProvider::pin() { t_pinned_ = rates_.load(); }

void RateProvider::unpin() { t_pinned_.reset(); }

extern "C" NUMEN_C_API std::uint32_t numen_abi_version(void) {
  try {
    return NUMEN_C_ABI_VERSION;
  } catch (...) {
    return 0;
  }
}

extern "C" NUMEN_C_API const char *numen_engine_version(void) {
  try {
    return "0.8.0";
  } catch (...) {
    return nullptr;
  }
}

extern "C" NUMEN_C_API numen_instance *numen_create(void) {
  try {
    auto engine = std::make_unique<numen::Numen>();
    auto provider = std::make_unique<RateProvider>();
    RateProvider *rawProvider = provider.get();
    engine->setCurrencyProvider(std::move(provider));
    return new numen_instance{std::move(engine), rawProvider};
  } catch (...) {
    return nullptr;
  }
}

extern "C" NUMEN_C_API void numen_destroy(numen_instance *instance) {
  try {
    delete instance;
  } catch (...) {
  }
}

extern "C" NUMEN_C_API numen_status numen_set_rates(numen_instance *instance, const numen_rate *rates,
                                                    std::size_t count) {
  try {
    if (!instance) return NUMEN_ERR_INVALID_ARGUMENT;
    if (!rates && count > 0) return NUMEN_ERR_INVALID_ARGUMENT;

    std::unordered_map<std::string, double> next;
    next.reserve(count);

    for (std::size_t i = 0; i < count; ++i) {
      const numen_rate &entry = rates[i];
      if (!entry.code) return NUMEN_ERR_INVALID_ARGUMENT;

      const std::string_view code(entry.code);
      if (!isValidUtf8(code)) return NUMEN_ERR_INVALID_ARGUMENT;
      if (!std::isfinite(entry.units_per_usd) || entry.units_per_usd <= 0) {
        return NUMEN_ERR_INVALID_ARGUMENT;
      }

      std::string key = normaliseRateCode(code);
      if (key.empty()) return NUMEN_ERR_INVALID_ARGUMENT;

      next.insert_or_assign(std::move(key), entry.units_per_usd);
    }

    instance->rateProvider->setRates(std::move(next));
    return NUMEN_OK;
  } catch (const std::exception &) {
    return NUMEN_ERR_INTERNAL;
  } catch (...) {
    return NUMEN_ERR_INTERNAL;
  }
}

extern "C" NUMEN_C_API numen_status numen_evaluate(numen_instance *instance, const char *expr,
                                                   std::size_t expr_len,
                                                   const numen_eval_options *options,
                                                   numen_result *out) {
  if (!out) return NUMEN_ERR_INVALID_ARGUMENT;

  const std::uint32_t structSize = out->struct_size;
  // Reject before clearing: a differently sized caller struct must not be overwritten wholesale.
  if (structSize != static_cast<std::uint32_t>(sizeof(numen_result))) {
    out->status = NUMEN_ERR_ABI_MISMATCH;
    return NUMEN_ERR_ABI_MISMATCH;
  }

  std::memset(out, 0, sizeof(*out));
  out->struct_size = structSize;

  try {
    return evaluateInto(instance, expr, expr_len, options, out);
  } catch (const std::exception &) {
    releaseResultFields(out);
    setInternalError(out);
    return NUMEN_ERR_INTERNAL;
  } catch (...) {
    releaseResultFields(out);
    setInternalError(out);
    return NUMEN_ERR_INTERNAL;
  }
}

extern "C" NUMEN_C_API void numen_result_free(numen_result *result) {
  if (!result) return;

  try {
    freeResultFields(result);
    std::memset(result, 0, sizeof(*result));
  } catch (...) {
  }
}

extern "C" NUMEN_C_API void numen_eval_options_init(numen_eval_options *options) {
  if (!options) return;

  try {
    std::memset(options, 0, sizeof(*options));
    options->struct_size = static_cast<std::uint32_t>(sizeof(numen_eval_options));
    options->locale = nullptr;
    options->strict = 0;
    options->implicit_currency = 1;
    options->implicit_unit = 1;
    options->implicit_timezone = 1;
    options->now_unix_seconds = 0;
    options->timezone = nullptr;
    options->relative_dates = 1;
    options->with_timezone = 1;
    options->date_format_neutral = 0;
    options->date_locale = nullptr;
  } catch (...) {
  }
}
