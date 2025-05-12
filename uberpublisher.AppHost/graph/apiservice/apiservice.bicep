@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cae_outputs_azure_container_apps_environment_default_domain string

param cae_outputs_azure_container_apps_environment_id string

param apiservice_containerimage string

param apiservice_containerport string

@secure()
param cache_password_value string

param cae_outputs_azure_container_registry_endpoint string

param cae_outputs_azure_container_registry_managed_identity_id string

resource apiservice 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'apiservice'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--cache'
          value: 'cache:6379,password=${cache_password_value}'
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(apiservice_containerport)
        transport: 'http'
      }
      registries: [
        {
          server: cae_outputs_azure_container_registry_endpoint
          identity: cae_outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: cae_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: apiservice_containerimage
          name: 'apiservice'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EXCEPTION_LOG_ATTRIBUTES'
              value: 'true'
            }
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EVENT_LOG_ATTRIBUTES'
              value: 'true'
            }
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'HTTP_PORTS'
              value: apiservice_containerport
            }
            {
              name: 'ConnectionStrings__cache'
              secretRef: 'connectionstrings--cache'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${cae_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}