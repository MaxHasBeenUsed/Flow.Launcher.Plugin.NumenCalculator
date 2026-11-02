#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include "numen_c.h"

#include <algorithm>
#include <cstring>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace numen_test {

// Raised when the harness cannot obtain a usable numen_c.dll. This is an
// infrastructure failure, never a statement about Numen behavior.
class AbiLoadError : public std::runtime_error {
public:
  using std::runtime_error::runtime_error;
};

// Owns the dynamically loaded module and every export the harness calls.
// Resolution happens once in load(); a missing export is an AbiLoadError.
struct NumenApi {
  using abi_version_fn = uint32_t (*)();
  using engine_version_fn = const char *(*)();
  using create_fn = numen_instance *(*)();
  using destroy_fn = void (*)(numen_instance *);
  using set_rates_fn = numen_status (*)(numen_instance *, const numen_rate *, size_t);
  using evaluate_fn = numen_status (*)(numen_instance *, const char *, size_t, const numen_eval_options *,
                                       numen_result *);
  using result_free_fn = void (*)(numen_result *);
  using eval_options_init_fn = void (*)(numen_eval_options *);

  struct Functions {
    abi_version_fn abi_version = nullptr;
    engine_version_fn engine_version = nullptr;
    create_fn create = nullptr;
    destroy_fn destroy = nullptr;
    set_rates_fn set_rates = nullptr;
    evaluate_fn evaluate = nullptr;
    result_free_fn result_free = nullptr;
    eval_options_init_fn eval_options_init = nullptr;
  };

  HMODULE module = nullptr;
  Functions fn{};

  NumenApi() = default;
  NumenApi(const NumenApi &) = delete;
  NumenApi &operator=(const NumenApi &) = delete;

  NumenApi(NumenApi &&other) noexcept : module(other.module), fn(other.fn) {
    other.module = nullptr;
    other.fn = {};
  }

  NumenApi &operator=(NumenApi &&other) noexcept {
    if (this != &other) {
      reset();
      module = other.module;
      fn = other.fn;
      other.module = nullptr;
      other.fn = {};
    }
    return *this;
  }

  ~NumenApi() { reset(); }

  // Search order: argv[1] when it is a path (not a `--flag`), then
  // NUMEN_C_DLL, then numen_c.dll next to this executable.
  static NumenApi load(int argc, char **argv) {
    std::string attempts;
    for (const std::string &path : candidate_paths(argc, argv)) {
      HMODULE module = LoadLibraryA(path.c_str());
      if (module == nullptr) {
        const DWORD error = GetLastError();
        attempts += "  " + path + " (LoadLibrary error " + std::to_string(error) + ")\n";
        continue;
      }

      NumenApi api;
      api.module = module;
      try {
        api.resolve();
        return api;
      } catch (const AbiLoadError &error) {
        api.reset();
        throw AbiLoadError(path + ": " + error.what());
      } catch (...) {
        api.reset();
        throw;
      }
    }

    throw AbiLoadError("failed to load numen_c.dll; tried:\n" + attempts);
  }

private:
  void reset() {
    if (module != nullptr) {
      FreeLibrary(module);
      module = nullptr;
    }
    fn = {};
  }

  void resolve() {
    fn.abi_version = get_export<abi_version_fn>(module, "numen_abi_version");
    fn.engine_version = get_export<engine_version_fn>(module, "numen_engine_version");
    fn.create = get_export<create_fn>(module, "numen_create");
    fn.destroy = get_export<destroy_fn>(module, "numen_destroy");
    fn.set_rates = get_export<set_rates_fn>(module, "numen_set_rates");
    fn.evaluate = get_export<evaluate_fn>(module, "numen_evaluate");
    fn.result_free = get_export<result_free_fn>(module, "numen_result_free");
    fn.eval_options_init = get_export<eval_options_init_fn>(module, "numen_eval_options_init");
  }

  template <typename Fn> static Fn get_export(HMODULE module, const char *name) {
    const FARPROC proc = GetProcAddress(module, name);
    if (proc == nullptr) {
      throw AbiLoadError(std::string("numen_c.dll is missing export \"") + name + "\"");
    }

    static_assert(sizeof(Fn) == sizeof(proc), "unexpected function pointer size");
    Fn fn = nullptr;
    std::memcpy(&fn, &proc, sizeof(fn));
    return fn;
  }

  static std::string executable_directory() {
    std::vector<char> buffer(MAX_PATH);
    for (;;) {
      const DWORD written = GetModuleFileNameA(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
      if (written == 0) {
        throw AbiLoadError("GetModuleFileNameA failed while locating numen_c.dll");
      }
      if (written < static_cast<DWORD>(buffer.size())) {
        return std::filesystem::path(buffer.data()).parent_path().string();
      }
      buffer.resize(buffer.size() * 2);
    }
  }

  static std::string environment_variable(const char *name) {
    const DWORD required = GetEnvironmentVariableA(name, nullptr, 0);
    if (required == 0) return {};

    std::vector<char> buffer(required);
    const DWORD written = GetEnvironmentVariableA(name, buffer.data(), required);
    if (written == 0 || written >= required) return {};
    return std::string(buffer.data(), written);
  }

  static std::vector<std::string> candidate_paths(int argc, char **argv) {
    std::vector<std::string> paths;

    const auto add = [&paths](std::string path) {
      if (path.empty()) return;
      if (std::find(paths.begin(), paths.end(), path) == paths.end()) {
        paths.push_back(std::move(path));
      }
    };

    if (argc > 1 && argv[1] != nullptr && argv[1][0] != '\0' && argv[1][0] != '-') {
      add(argv[1]);
    }

    const std::string from_env = environment_variable("NUMEN_C_DLL");
    if (!from_env.empty()) { add(from_env); }

    add((std::filesystem::path(executable_directory()) / "numen_c.dll").string());
    return paths;
  }
};

} // namespace numen_test
