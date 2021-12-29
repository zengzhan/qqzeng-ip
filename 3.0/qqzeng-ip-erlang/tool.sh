#!/bin/bash
help(){
	echo "sh tool.sh rebar3 new mod aa path=src"
}
case $1 in
rebar3)
    CMD="./rebar3"
	i=0
	for par in $@; do
	if [ $i -ne 0 ];then
		CMD="$CMD $par"
	fi
	let i=i+1
	done
    CMD=$CMD" author_name='admin' apache_license=''"
	eval $CMD
	if [ $? -ne 0 ]; then
		echo "eval {$CMD} ERROR !!!!!"
		exit 1
	else
		echo "eval {$CMD} SUCCESS "
	fi
;;
*) help;;
esac