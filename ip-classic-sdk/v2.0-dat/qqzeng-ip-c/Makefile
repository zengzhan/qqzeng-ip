# qqzeng-ip 2.0 (.dat) C 版构建
# make          -> 构建 demo (若有 verify.c 则同时构建 verify)
# make test     -> 运行 demo
# make clean

CC      ?= cc
CFLAGS  ?= -O2 -std=c11 -Wall -Wextra -Wpedantic
LDFLAGS ?=

TARGETS := demo $(if $(wildcard verify.c),verify,)

ALL: $(TARGETS)

demo: IpSearch2Fast.c Test.c
	$(CC) $(CFLAGS) -o demo IpSearch2Fast.c Test.c

verify: IpSearch2Fast.c verify.c
	$(CC) $(CFLAGS) -o verify IpSearch2Fast.c verify.c -lpthread

test: demo
	./demo

clean:
	rm -f demo verify

.PHONY: ALL test clean
