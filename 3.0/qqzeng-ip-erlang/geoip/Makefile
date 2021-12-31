all: compile dialyzer test

###===================================================================
### build
###===================================================================
.PHONY: co compile es escriptize run

co:compile
compile:
	./rebar3 compile

es:escriptize
escriptize: clean
	./rebar3 escriptize

### clean
.PHONY: clean distclean
clean:
	./rebar3 clean

distclean:
	./rebar3 clean -a

###===================================================================
### test
###===================================================================
.PHONY: test eunit ct testclean

test: epmd dialyzer ct eunit

eunit: epmd
	./rebar3 do eunit -d test/eunit -v

ct: epmd
	sh crates/build_crates.sh clippy
	sh crates/build_crates.sh test
	./rebar3 do ct --dir test/ct -v --config test/ct/ct.config --sys_config config/test.config

testclean:
	@rm -fr _build/test

shell: epmd config
	./rebar3 as test shell

config: epmd
	#./tool.sh replace_config

dialyzer: epmd
	./rebar3 do dialyzer

tar: epmd
	rm -rf _build/prod
	./rebar3 as prod release
	./rebar3 as prod tar
###===================================================================
### other
###===================================================================
.PHONY: epmd

epmd:
	@pgrep epmd 2> /dev/null > /dev/null || epmd -daemon || true
