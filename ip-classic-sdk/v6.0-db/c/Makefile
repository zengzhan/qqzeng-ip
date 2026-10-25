# qqzeng-ip 6.0 C SDK
# make            构建 main (若有 verify.c 则同时构建 verify)
# make clean

CC ?= cc
CFLAGS ?= -O2 -std=c11 -Wall -Wextra -Wpedantic
SRC = src/qqzeng_ip_search.c

TARGETS := main $(if $(wildcard verify.c),verify,)

all: $(TARGETS)

verify: verify.c $(SRC) src/qqzeng_ip_search.h
	$(CC) $(CFLAGS) -o $@ verify.c $(SRC)

main: main.c $(SRC) src/qqzeng_ip_search.h
	$(CC) $(CFLAGS) -o $@ main.c $(SRC)

DB ?= qqzeng-ip-6.0-global.db
VEC ?= ../build/global_vec.bin
run: $(TARGETS)
	$(if $(wildcard verify.c),./verify $(DB) $(VEC) accuracy && ./verify $(DB) $(VEC) edge && ./verify $(DB) $(VEC) bench && ./verify $(DB) $(VEC) conc,)
	./main $(DB)

clean:
	rm -f verify main
