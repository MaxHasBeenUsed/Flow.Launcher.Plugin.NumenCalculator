#pragma once

// The native harnesses drive the real DLL, so no C++ currency provider is
// linked into them. This header only carries the rate tables both executables
// inject through numen_set_rates so the expected values stay in one place.

#include "numen_c.h"

#include <array>

namespace numen_test {

// §13.1: usd=1, eur=0.9.
inline constexpr std::array<numen_rate, 2> kAbiRates{{
    numen_rate{"usd", 1.0},
    numen_rate{"eur", 0.9},
}};

// §13.2 curated currency-success rates, units per USD.
inline constexpr std::array<numen_rate, 6> kCuratedRates{{
    numen_rate{"usd", 1.0},
    numen_rate{"eur", 0.9234567},
    numen_rate{"gbp", 0.7891234},
    numen_rate{"btc", 0.0000117},
    numen_rate{"xmr", 0.005},
    numen_rate{"shib", 100000.0},
}};

} // namespace numen_test
