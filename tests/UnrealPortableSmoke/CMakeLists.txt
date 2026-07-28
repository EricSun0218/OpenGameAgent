cmake_minimum_required(VERSION 3.20)

project(GameAgentUnrealPortableSmoke LANGUAGES C CXX)

include(CTest)

set(GAME_AGENT_UNREAL_ROOT
    "${CMAKE_CURRENT_SOURCE_DIR}/../../engines/unreal")

add_library(GameAgentWire STATIC
    "${GAME_AGENT_UNREAL_ROOT}/Source/GameAgentRuntime/Private/GameAgentWireProtocol.cpp")

target_include_directories(GameAgentWire PUBLIC
    "${GAME_AGENT_UNREAL_ROOT}/Source/GameAgentRuntime/Public")

target_compile_features(GameAgentWire PUBLIC cxx_std_17)

add_library(GameAgentAbiC11Smoke OBJECT
    "AbiC11Smoke.c")

target_include_directories(GameAgentAbiC11Smoke PRIVATE
    "${GAME_AGENT_UNREAL_ROOT}/Source/GameAgentRuntime/Public")

target_compile_features(GameAgentAbiC11Smoke PRIVATE c_std_11)

add_executable(GameAgentUnrealPortableSmoke
    $<TARGET_OBJECTS:GameAgentAbiC11Smoke>
    "UnrealPortableSmoke.cpp")

target_link_libraries(GameAgentUnrealPortableSmoke PRIVATE GameAgentWire)
target_compile_features(GameAgentUnrealPortableSmoke PRIVATE cxx_std_17)

if(MSVC)
    target_compile_options(GameAgentWire PRIVATE /W4 /WX /permissive-)
    target_compile_options(GameAgentAbiC11Smoke PRIVATE /W4 /WX)
    target_compile_options(GameAgentUnrealPortableSmoke PRIVATE /W4 /WX /permissive-)
else()
    target_compile_options(GameAgentWire PRIVATE
        -Wall
        -Wextra
        -Wpedantic
        -Werror)
    target_compile_options(GameAgentAbiC11Smoke PRIVATE
        -Wall
        -Wextra
        -Wpedantic
        -Werror)
    target_compile_options(GameAgentUnrealPortableSmoke PRIVATE
        -Wall
        -Wextra
        -Wpedantic
        -Werror)
endif()

add_test(
    NAME GameAgent.Unreal.PortableWire
    COMMAND GameAgentUnrealPortableSmoke
        "${CMAKE_CURRENT_SOURCE_DIR}/../../fixtures/v0.2/valid/json-only-tool-loop"
        "${CMAKE_CURRENT_SOURCE_DIR}/fixtures/runtime-event.json")
