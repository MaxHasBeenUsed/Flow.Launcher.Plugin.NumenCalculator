cmake_minimum_required(VERSION 3.21)

# Added from the parent build, after the shared numen_c target exists.
if(NOT TARGET numen_c)
  message(FATAL_ERROR "tests/native requires the parent numen_c target")
endif()

get_target_property(NUMEN_C_TARGET_TYPE numen_c TYPE)
if(NOT NUMEN_C_TARGET_TYPE STREQUAL "SHARED_LIBRARY")
  message(FATAL_ERROR "tests/native requires numen_c to be a SHARED_LIBRARY (got ${NUMEN_C_TARGET_TYPE})")
endif()

function(add_numen_native_test target)
  add_executable(${target} ${ARGN})
  add_dependencies(${target} numen_c)

  target_compile_features(${target} PRIVATE cxx_std_20)
  set_target_properties(${target} PROPERTIES CXX_EXTENSIONS OFF)
  target_include_directories(${target} PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../../native/include")

  if(MSVC)
    target_compile_options(${target} PRIVATE /utf-8)
  endif()

  add_custom_command(TARGET ${target} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "$<TARGET_FILE:numen_c>"
      "$<TARGET_FILE_DIR:${target}>/numen_c.dll"
    VERBATIM)
endfunction()

add_numen_native_test(numen_abi_tests numen_abi_tests.cpp)
add_numen_native_test(numen_corpus_diff numen_corpus_diff.cpp)

find_package(Threads REQUIRED)
target_link_libraries(numen_abi_tests PRIVATE Threads::Threads)

target_compile_definitions(numen_corpus_diff PRIVATE
  "NUMEN_C_CORPUS_DIR=\"${CMAKE_CURRENT_SOURCE_DIR}/../../numen/tests/corpus\"")

add_test(NAME numen_abi_tests COMMAND numen_abi_tests)
add_test(NAME numen_corpus_diff COMMAND numen_corpus_diff --suite=corpus)
add_test(NAME numen_corpus_diff_strict COMMAND numen_corpus_diff --suite=strict)
add_test(NAME numen_corpus_diff_rates COMMAND numen_corpus_diff --suite=rates)
