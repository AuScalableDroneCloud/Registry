#!/bin/bash

mkdir -p yaml

# Generate initialize.sql and appsettings.json,
# from templates using .env
./run.sh --dry &> /dev/null

function subst_template()
{
  #Use envsubst to apply variables to template .yaml files
  #$1 = filename.yaml

  #Runs envsubst but skips vars not defined in env https://unix.stackexchange.com/a/492778/17168
  cat templates/$1 | envsubst "$(env | cut -d= -f1 | sed -e 's/^/$/')" > yaml/$1
  echo "Applied env to template: templates/$1 => yaml/$1"
}

function apply_template()
{
  #Substitute env vars
  subst_template $1
  #Apply to cluster
  kubectl apply -f yaml/$1 --namespace=dronedb
}

#Cat file content indented by 4 spaces into variables
export INITIALIZE_SQL_CONTENT=$(cat initialize.sql | sed 's/\(.*\)/    \1/')
export APPSETTINGS_JSON_CONTENT=$(cat appsettings.json | sed 's/\(.*\)/    \1/')

kubectl create namespace dronedb

apply_template db-configmap.yaml
apply_template db-persistentvolumeclaim.yaml
apply_template db-deployment.yaml
apply_template db-service.yaml

apply_template phpmyadmin-deployment.yaml
apply_template phpmyadmin-service.yaml

apply_template registry-configmap.yaml
apply_template registry-persistentvolumeclaim.yaml
apply_template registry-deployment.yaml
apply_template registry-service.yaml

apply_template ingress.yaml

