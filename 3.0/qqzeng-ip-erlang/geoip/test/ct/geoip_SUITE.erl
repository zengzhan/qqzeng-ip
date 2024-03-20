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
-include("geoip.hrl").
-compile(export_all).

all() ->
    [query, query_friendly, bench].

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
    ok = meck:new(geoip, [passthrough, non_strict, no_link]),
    ok.

expect() ->
    ok = meck:expect(geoip, test, fun() -> {ok, 1} end).

del_meck() ->
    meck:unload().

query(_Config) ->
    ?assertEqual({ok, <<"|CloudFlareDNS||||APNIC|||||">>}, geoip:query(<<"1.1.1.1">>)),
    ?assertEqual({ok, <<"|GoogleDNS||||GoogleDNS|||||">>}, geoip:query(<<"8.8.8.8">>)),
    ?assertEqual({ok, <<"|保留|全球|旗舰版||qqzeng-ip||最新版|2021-12-01|880995|"/utf8>>},
                 geoip:query(<<"255.255.255.255">>)),
    ?assertEqual({error, <<"Ip invalid">>}, geoip:query(<<"-255.255.255.255">>)),
    ?assertEqual({error, <<"Ip invalid">>}, geoip:query(<<"256.255.255.255">>)),
    ?assertEqual({error, <<"Ip invalid">>}, geoip:query(<<"xk256.255.255.255">>)),
    ?assertEqual({error, <<"Ip invalid">>}, geoip:query(<<"256.255.a.255">>)),
    ?assertEqual({error, <<"Ip invalid">>}, geoip:query(<<"25.255..255">>)),
    ok.

query_friendly(_) ->
    %%欧洲|英国||||||United Kingdom|GB|-3.435973|55.378051
    ?assertEqual({ok,
      #'GeoIPRes'{continent = <<"欧洲"/utf8>>,
                   country = <<"英国"/utf8>>,
                   province = <<>>,
                   city = <<>>,
                   district = <<>>,
                   isp = <<>>,
                   area_code = <<>>,
                   country_english = <<"United Kingdom">>,
                   country_code = <<"GB">>,
                   longitude = <<"-3.435973">>,
                   latitude = <<"55.378051">>}},
                 geoip:query_friendly(<<"25.255.25.255">>)),
  ?assertEqual({ok,
    #'GeoIPRes'{continent = <<"亚洲"/utf8>>,
      country = <<"中国"/utf8>>,
      province = <<"广东"/utf8>>,
      city = <<"广州"/utf8>>,
      district = <<>>,
      isp = <<"电信"/utf8>>,
      area_code = <<"440100">>,
      country_english = <<"China">>,
      country_code = <<"CN">>,
      longitude = <<"113.280637">>,
      latitude = <<"23.125178">>}},
    geoip:query_friendly(<<"14.215.177.39">>)),
    ok.
bench(_) ->
  bench_(1, 1000000),
  bench_(1000000000, 1001000000),
  ok.
bench_(S, E) ->
  [ bench_(N)|| N<- lists:seq(S,E)].
bench_(N) ->
  Ip = int_to_ip(N),
  ?assertMatch({N, {ok,_}}, {N, geoip:query_friendly(Ip)}).

get_dir() ->
  ct_util:get_start_dir().
int_to_ip(Num) ->
  B1 = (Num band 2#11111111000000000000000000000000) bsr 24,
  B2 = (Num band 2#00000000111111110000000000000000) bsr 16,
  B3 = (Num band 2#00000000000000001111111100000000) bsr 8,
  B4 = Num band 2#00000000000000000000000011111111,
  <<
    (integer_to_binary(B1))/binary, ".",
    (integer_to_binary(B2))/binary, ".",
    (integer_to_binary(B3))/binary, ".",
    (integer_to_binary(B4))/binary
  >>.

