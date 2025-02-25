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

-module(geoip).

-author("yangcancai").

-include("geoip.hrl").

-export([start/0, query/1, query_friendly/1, stop/0]).

-type geoip_res() :: #'GeoIPRes'{}.

start() ->
    File = application:get_env(geoip, dat_file, "qqzeng-ip-3.0-ultimate.dat"),
    {ok, Ref} =
        geoip_nif:new(
            erlang:list_to_binary(File)),
    persistent_term:put(?MODULE, Ref),
    Ref.

stop() ->
    Ref = ref(),
    geoip_nif:clear(Ref).

ref() ->
    case catch persistent_term:get(?MODULE) of
        {'EXIT', _} ->
            start();
        Ref ->
            Ref
    end.

-spec query(Ip :: binary()) -> {ok, binary()} | {error, binary()}.
query(Ip) when is_binary(Ip) ->
    geoip_nif:query(ref(), Ip).

-spec query_friendly(Ip :: binary()) -> {ok, geoip_res()} | {error, binary()}.
query_friendly(Ip) when is_binary(Ip) ->
    geoip_nif:query_friendly(ref(), Ip).
