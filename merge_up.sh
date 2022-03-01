#!/bin/bash
git remote add upstream https://github.com/DroneDB/Registry.git
git fetch upstream
git checkout master
git merge upstream/master

