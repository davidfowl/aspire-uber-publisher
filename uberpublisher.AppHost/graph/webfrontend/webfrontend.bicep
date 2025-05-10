@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cae_outputs_azure_container_apps_environment_default_domain string

param cae_outputs_azure_container_apps_environment_id string

param webfrontend_containerimage string

param webfrontend_identity_outputs_id string

param webfrontend_containerport string

param storage_outputs_blobendpoint string

param webfrontend_identity_outputs_clientid string

param cae_outputs_azure_container_registry_endpoint string

param cae_outputs_azure_container_registry_managed_identity_id string

resource webfrontend 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'webfrontend'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(webfrontend_containerport)
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
          image: webfrontend_containerimage
          name: 'webfrontend'
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
              value: webfrontend_containerport
            }
            {
              name: 'services__apiservice__http__0'
              value: 'http://apiservice.internal.${cae_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__apiservice__https__0'
              value: 'https://apiservice.internal.${cae_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'ConnectionStrings__blobs'
              value: storage_outputs_blobendpoint
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: webfrontend_identity_outputs_clientid
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
      '${webfrontend_identity_outputs_id}': { }
      '${cae_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}