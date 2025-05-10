@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string

param tags object = { }

resource cae_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: take('cae_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource cae_acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: take('caeacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: tags
}

resource cae_acr_cae_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cae_acr.id, cae_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: cae_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: cae_acr
}

resource cae_law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: take('caelaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource cae 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: take('cae${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: cae_law.properties.customerId
        sharedKey: cae_law.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
  tags: tags
}

resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2024-10-02-preview' = {
  name: 'aspire-dashboard'
  properties: {
    componentType: 'AspireDashboard'
  }
  parent: cae
}

resource cae_Contributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cae.id, userPrincipalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c'))
  properties: {
    principalId: userPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
  }
  scope: cae
}

output MANAGED_IDENTITY_NAME string = cae_mi.name

output MANAGED_IDENTITY_PRINCIPAL_ID string = cae_mi.properties.principalId

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = cae_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = cae_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = cae_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = cae_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = cae_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = cae.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = cae.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = cae.properties.defaultDomain