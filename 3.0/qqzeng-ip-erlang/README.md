geoip
----

![CI](https://github.com/yangcancai/qqzeng-ip/actions/workflows/ci.yml/badge.svg)

## Install(rebar3)
[
{dep,[
    {'qqzeng-ip-erlang',{git_subdir, "https://github.com/yangcancai/qqzeng-ip.git", {branch, "master"},"3.0/qqzeng-ip-erlang"}}
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