# qqzeng-ip 2.0 (.dat) C++ 版构建
# make          -> 构建 demo (若有 verify.cpp 则同时构建 verify)
# make test     -> 运行 demo
# make clean

CXX      ?= c++
CXXFLAGS ?= -O2 -std=c++11 -Wall -Wextra -Wpedantic

TARGETS := demo $(if $(wildcard verify.cpp),verify,)

ALL: $(TARGETS)

demo: IPLocator.h IPLocator.cpp main.cpp
	$(CXX) $(CXXFLAGS) -o demo IPLocator.cpp main.cpp

verify: IPLocator.h IPLocator.cpp verify.cpp
	$(CXX) $(CXXFLAGS) -o verify IPLocator.cpp verify.cpp

test: demo
	./demo

clean:
	rm -f demo verify

.PHONY: ALL test clean
