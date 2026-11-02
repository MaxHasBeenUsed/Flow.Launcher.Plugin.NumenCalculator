cmake_minimum_required(VERSION 3.21)
cmake_policy(SET CMP0091 NEW)
project(numen_c LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(BUILD_REPL OFF CACHE BOOL "" FORCE)
set(BUILD_REPL_CURRENCY_PROVIDER OFF CACHE BOOL "" FORCE)
set(NUMEN_HARDENING ON CACHE BOOL "" FORCE)
set(NUMEN_USE_DATE_TZ OFF CACHE BOOL "" FORCE)

add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../numen
                 ${CMAKE_CURRENT_BINARY_DIR}/numen EXCLUDE_FROM_ALL)

add_library(numen_c SHARED numen_c.cpp)
target_compile_definitions(numen_c PRIVATE NUMEN_C_BUILD)
target_include_directories(numen_c PRIVATE include)
target_link_libraries(numen_c PRIVATE numen::numen)
target_compile_options(numen_c PRIVATE
  /std:c++latest /utf-8 /permissive- /W4 /guard:cf /EHsc /D_CRT_SECURE_NO_WARNINGS)
target_link_options(numen_c PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
set_target_properties(numen_c PROPERTIES OUTPUT_NAME numen_c PREFIX "" DEBUG_POSTFIX "")

option(NUMEN_C_BUILD_TESTS "Build C ABI tests" OFF)
if (NUMEN_C_BUILD_TESTS)
  enable_testing()
  add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../tests/native"
                   "${CMAKE_CURRENT_BINARY_DIR}/tests")
endif()
