#!/bin/bash
mkdir -p ./priv
cmd_exists(){
	local ret=0;
	command -v $1 >/dev/null 2>&1 || { local ret=1; }
	if [ "$ret" -ne 0 ]; then
		return 0;
	fi
	return 1
}
cmd_exists cargo
if [ $? -eq '0' ]; then
    curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
    source $HOME/.cargo/env
else
    echo "Rust already install."
fi
export CARGO_TARGET_DIR="$(pwd)/target"
echo $CARGO_TARGET_DIR
touch crates/geoip/build.rs
build(){
    mkdir -p ./priv
    cargo build --manifest-path=crates/geoip/Cargo.toml --release
    sh -c "cp $(cat crates/geoip/libpath) ./priv/libgeoip.so "
}
test(){
    cargo build --manifest-path=crates/geoip/Cargo.toml
    cargo test --manifest-path=crates/geoip/Cargo.toml 
}
clippy(){
    cargo clippy --manifest-path=crates/geoip/Cargo.toml 
}
help(){
    echo "sh build_crates.sh <command> :"
    echo "build              - do cargo build and cp libpath to priv"
    echo "test               - do cargo test"
    echo "clippy             - do cargo clippy to improve your rust code"
    echo "bench              - do cargo bench"
    echo "help               - help to use command"
}
bench(){
    cargo bench --manifest-path=crates/geoip/Cargo.toml 
}
fmt(){
    cargo fmt --manifest-path=crates/geoip/Cargo.toml 
}
update(){
    cargo update --manifest-path=crates/geoip/Cargo.toml 
}
case $1 in
fmt) fmt;;
bench) bench;;
build) build;;
test) test;;
update) update;;
clippy) clippy;;
*) help;;
esac