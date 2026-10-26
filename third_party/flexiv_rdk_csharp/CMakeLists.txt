cmake_minimum_required(VERSION 3.16)
project(FlexivRdkCSharp VERSION 1.9.3)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# LOCAL CHANGE: RDK version is a parameter (robot software 3.10 -> RDK 1.8, 3.11 -> RDK 1.9).
set(FLEXIV_RDK_VERSION "1.9" CACHE STRING "flexiv_rdk version to build against (major.minor)")
string(REPLACE "." ";" _rdk_ver_parts "${FLEXIV_RDK_VERSION}")
list(GET _rdk_ver_parts 0 _rdk_major)
list(GET _rdk_ver_parts 1 _rdk_minor)
math(EXPR FLEXIV_RDK_API "${_rdk_major} * 100 + ${_rdk_minor}")
message(STATUS "Building wrapper against flexiv_rdk ${FLEXIV_RDK_VERSION} (FLEXIV_RDK_API=${FLEXIV_RDK_API})")

list(APPEND CMAKE_PREFIX_PATH "${CMAKE_SOURCE_DIR}/install/rdk_install")
find_package(flexiv_rdk ${FLEXIV_RDK_VERSION} REQUIRED)

include(GNUInstallDirs)

set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib" CACHE PATH "Archive output dir.")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib" CACHE PATH "Library output dir.")
set(CMAKE_PDB_OUTPUT_DIRECTORY     "${CMAKE_BINARY_DIR}/bin" CACHE PATH "PDB (MSVC debug symbol)output dir.")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin" CACHE PATH "Executable/dll output dir.")

if(CMAKE_INSTALL_PREFIX_INITIALIZED_TO_DEFAULT)
    set(CMAKE_INSTALL_PREFIX "${CMAKE_SOURCE_DIR}/install" CACHE PATH "Default install path" FORCE)
endif()
add_subdirectory(cpp_wrapper)
