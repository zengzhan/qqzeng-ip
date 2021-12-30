%%%-------------------------------------------------------------------
%%% @author yangcancai

%%% Copyright (c) 2021 by yangcancai(yangcancai0112@gmail.com), All Rights Reserved.
%%%
%%% Licensed under the Apache License, Version 2.0 (the "License");
%%% you may not use this file except in compliance with the License.
%%% You may obtain a copy of the License at
%%%
%%%       https://www.apache.org/licenses/LICENSE-2.0
%%%
%%% Unless required by applicable law or agreed to in writing, software
%%% distributed under the License is distributed on an "AS IS" BASIS,
%%% WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
%%% See the License for the specific language governing permissions and
%%% limitations under the License.
%%%
   
%%% @doc
%%%
%%% @end
%%% Created : 2021-12-29T02:29:40+00:00
%%%-------------------------------------------------------------------

-module(geoip_SUITE).

-author("yangcancai").

-include("geoip_ct.hrl").

-compile(export_all).

all() ->
    [query].

init_per_suite(Config) ->
    Path = get_dir(),
    application:set_env(geoip, dat_file, filename:join(Path, "qqzeng-ip-3.0-ultimate.dat")),
    {ok, _} = application:ensure_all_started(geoip),
    new_meck(),
    Config.

end_per_suite(Config) ->
    del_meck(),
    application:stop(geoip),
    Config.

init_per_testcase(_Case, Config) ->
    Config.

end_per_testcase(_Case, _Config) ->
    ok.

new_meck() ->
    ok = meck:new(geoip, [passthrough,non_strict, no_link]),
    ok.

expect() ->
    ok = meck:expect(geoip, test, fun() -> {ok, 1} end).

del_meck() ->
    meck:unload().

query(_Config) ->
    ?assertEqual({ok,<<"|CloudFlareDNS||||APNIC|||||">>}, geoip:query(<<"1.1.1.1">>)),
    ?assertEqual({ok,<<"|GoogleDNS||||GoogleDNS|||||">>}, geoip:query(<<"8.8.8.8">>)),
    ?assertEqual({ok,<<"|保留|全球|旗舰版||qqzeng-ip||最新版|2021-12-01|880995|"/utf8>>}, geoip:query(<<"255.255.255.255">>)),
    ?assertEqual({error,<<"Ip invalid">>}, geoip:query(<<"-255.255.255.255">>)),
    ?assertEqual({error,<<"Ip invalid">>}, geoip:query(<<"256.255.255.255">>)),
    ?assertEqual({error,<<"Ip invalid">>}, geoip:query(<<"xk256.255.255.255">>)),
    ?assertEqual({error,<<"Ip invalid">>}, geoip:query(<<"256.255.a.255">>)),
    ?assertEqual({error,<<"Ip invalid">>}, geoip:query(<<"25.255..255">>)),
    ok.

get_dir() ->
    {ok, Rep} = file:get_cwd(),
    [Path, _] = string:split(Rep, "_build"),
    Path.
