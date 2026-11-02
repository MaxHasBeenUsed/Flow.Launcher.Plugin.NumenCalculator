#ifndef NUMEN_C_H
#define NUMEN_C_H

#include <stddef.h>
#include <stdint.h>

#define NUMEN_C_ABI_VERSION 1u

#ifdef _WIN32
#  ifdef NUMEN_C_BUILD
#    define NUMEN_C_API __declspec(dllexport)
#  else
#    define NUMEN_C_API __declspec(dllimport)
#  endif
#else
#  define NUMEN_C_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct numen_instance numen_instance;

typedef enum numen_status {
    NUMEN_OK = 0,
    NUMEN_ERR_INVALID_ARGUMENT = 1,
    NUMEN_ERR_ABI_MISMATCH = 2,
    NUMEN_ERR_EXPRESSION = 3,
    NUMEN_ERR_INTERNAL = 4
} numen_status;

typedef enum numen_value_kind {
    NUMEN_VALUE_NUMBER = 0,
    NUMEN_VALUE_DATETIME = 1,
    NUMEN_VALUE_DURATION = 2,
    NUMEN_VALUE_BOOLEAN = 3,
    NUMEN_VALUE_TEXT = 4
} numen_value_kind;

typedef enum numen_number_format {
    NUMEN_FORMAT_DECIMAL = 0,
    NUMEN_FORMAT_HEXADECIMAL = 1,
    NUMEN_FORMAT_BINARY = 2,
    NUMEN_FORMAT_OCTAL = 3
} numen_number_format;

typedef enum numen_conversion_kind {
    NUMEN_CONVERSION_NONE = 0,
    NUMEN_CONVERSION_UNIT = 1,
    NUMEN_CONVERSION_TIMEZONE = 2,
    NUMEN_CONVERSION_CONVERTER = 3
} numen_conversion_kind;

typedef struct numen_rate {
    const char *code;       /* null-terminated UTF-8; copied by the call */
    double units_per_usd;   /* > 0, finite */
} numen_rate;

typedef struct numen_eval_options {
    uint32_t struct_size;              /* sizeof(numen_eval_options) */
    const char *locale;                /* NULL/empty = system; POSIX or BCP 47 */
    int32_t strict;                    /* 1 */
    int32_t implicit_currency;         /* 1 */
    int32_t implicit_unit;             /* 1 */
    int32_t implicit_timezone;         /* 1 */
    int64_t now_unix_seconds;          /* 0 = system clock */
    const char *timezone;              /* NULL/empty = current zone; IANA name */
    int32_t relative_dates;            /* 1 = Numen default */
    int32_t with_timezone;             /* 1 = Numen default */
    int32_t date_format_neutral;       /* 0 = Local, 1 = Neutral */
    const char *date_locale;           /* NULL/empty = locale */
} numen_eval_options;

typedef struct numen_conversion {
    int32_t kind;                      /* numen_conversion_kind */
    int32_t implicit;
    int32_t has_from;
    char *from_text;                   /* malloc-owned; UTF-8 */
    char *to_text;                     /* malloc-owned; UTF-8 */
    int32_t from_offset_seconds;
    int32_t to_offset_seconds;
} numen_conversion;

typedef struct numen_result {
    uint32_t struct_size;              /* sizeof(numen_result) */
    numen_status status;
    numen_value_kind kind;

    char *error;                       /* NULL on success; otherwise malloc-owned */
    char *display_text;                /* exact Numen rendering; malloc-owned */

    /* Number */
    double number_value;
    char *number_text;                 /* formatted magnitude without unit */
    int32_t number_format;             /* numen_number_format */
    int32_t is_percentage;
    int32_t has_unit;
    char *unit_raw;
    char *unit_render;
    char *unit_name;
    int32_t unit_decimals;             /* -1 = unset */
    int32_t unit_symbol_prefix;

    /* DateTime */
    int64_t unix_seconds;
    int32_t nanos;
    int32_t offset_seconds;
    char *timezone_name;               /* NULL = current/local */
    char *rfc3339;
    char *timezone_string;

    /* Duration */
    int64_t years;
    int64_t months;
    int64_t seconds;
    int64_t subsecond_nanos;
    uint32_t present_mask;             /* bit 0 years, 1 months, 2 seconds, 3 subsecond */
    double total_seconds;

    /* Boolean / Text */
    int32_t boolean_value;
    char *text_value;

    /* Conversion */
    int32_t has_conversion;
    numen_conversion conversion;
} numen_result;

NUMEN_C_API uint32_t numen_abi_version(void);
NUMEN_C_API const char *numen_engine_version(void);
NUMEN_C_API numen_instance *numen_create(void);
NUMEN_C_API void numen_destroy(numen_instance *instance);
NUMEN_C_API numen_status numen_set_rates(numen_instance *instance,
                                         const numen_rate *rates, size_t count);
NUMEN_C_API numen_status numen_evaluate(numen_instance *instance,
                                        const char *expr, size_t expr_len,
                                        const numen_eval_options *options,
                                        numen_result *out);
NUMEN_C_API void numen_result_free(numen_result *result);
NUMEN_C_API void numen_eval_options_init(numen_eval_options *options);

#ifdef __cplusplus
}
#endif

#endif
