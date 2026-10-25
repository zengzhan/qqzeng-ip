cmake_minimum_required(VERSION 3.10)
project(qqzeng_ip_db C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -Wall -Wextra -Wpedantic")

add_library(qqzeng_ip_db src/qqzeng_ip_search.c)
target_include_directories(qqzeng_ip_db PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/src)

add_executable(qqzeng_ip_main main.c)
target_link_libraries(qqzeng_ip_main PRIVATE qqzeng_ip_db)

if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/verify.c")
    find_package(Threads REQUIRED)
    add_executable(verify verify.c)
    target_link_libraries(verify PRIVATE qqzeng_ip_db Threads::Threads)
endif()
