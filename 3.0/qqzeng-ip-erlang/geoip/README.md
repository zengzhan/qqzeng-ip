geoip
----

![CI](https://github.com/yangcancai/qqzeng-ip/actions/workflows/ci.yml/badge.svg)

## Install(rebar3)
[
{dep,[
    {'geoip',{git_subdir, "https://github.com/zengzhan/qqzeng-ip.git", {branch, "master"},"3.
0/qqzeng-ip-erlang"}}
]}
]

## Where qqzeng-ip-3.0-ultimate.dat?

### Default file and directory
```text
Copy it to the same level with rebar.config in local environment and where boot in production
```
### Set `dat_file` Env
```erlang
[{
    geoip,[{dat_file, "{BIN_DIR}/qqzeng-ip-3.0-ultimate.dat"}]
}].
```

## How to use?

```erlang

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
```