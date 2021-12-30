-module(geoip_nif).

%% API
-export([
    new/1,  %% new resource
    query/2,
    clear/1 %% clear resource
]).
%% Native library support
-export([load/0]).

-on_load load/0.

-opaque geoip() :: reference().

-export_type([geoip/0]).


-spec new(File :: binary()) -> {ok, Ref :: geoip()} | {error, Reason :: binary()}.
new(_File) ->
    not_loaded(?LINE).

-spec clear(Ref :: geoip()) -> ok.
clear(_Ref) ->
    not_loaded(?LINE).
-spec 'query'(Ref :: geoip(), Ip:: binary()) -> binary().
'query'(_Ref, _Ip) ->
    not_loaded(?LINE).

%% @private
load() ->
    erlang:load_nif(
        filename:join(priv(), "libgeoip"), none).

not_loaded(Line) ->
    erlang:nif_error({error, {not_loaded, [{module, ?MODULE}, {line, Line}]}}).

priv() ->
    case code:priv_dir(?MODULE) of
        {error, _} ->
            EbinDir =
                filename:dirname(
                    code:which(?MODULE)),
            AppPath = filename:dirname(EbinDir),
            filename:join(AppPath, "priv");
        Path ->
            Path
    end.