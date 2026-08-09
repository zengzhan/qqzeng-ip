#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
JAVA_HOME=$(ls -d /opt/homebrew/Cellar/openjdk@21/*/libexec/openjdk.jdk/Contents/Home /opt/homebrew/opt/openjdk@21 /opt/homebrew/opt/openjdk /Library/Java/JavaVirtualMachines/*/Contents/Home 2>/dev/null | head -1)
if [ -z "$JAVA_HOME" ]; then
    JAVA_HOME=$(find / -name "javac" -type f 2>/dev/null | head -1 | xargs dirname | xargs dirname)
fi
exec "$JAVA_HOME/bin/java" -cp "$SCRIPT_DIR/java_build" com.qqzeng.qzdb.BatchQuery "$@"
