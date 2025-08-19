#!/bin/bash

# First parameter is the sub-directory-absolute-path
# Second parameter is the link of the repo

# A smart split to get the repo-name, with / as a separator
REPO_NAME="$(echo $2 | grep -oE '[^/]+$')"

git init $REPO_NAME
cd $REPO_NAME

git remote add origin $2
git config core.sparsecheckout true

# Specipy the sub directory
echo "$1/*" >> .git/info/sparse-checkout
# then get it, the depth is the way too far wher you can go...
git pull origin master

# and badaboum, you only get your sub-dir
# this script is functionnal for github/gitlab and bitbucket
