param accountName string
param databaseName string
param containerName string = 'analyst-workspace'

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: accountName
}
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' existing = {
  parent: account
  name: databaseName
}
resource workspace 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: containerName
  properties: {
    options: {}
    resource: {
      id: containerName
      partitionKey: {
        kind: 'Hash'
        paths: ['/id']
        version: 2
      }
      indexingPolicy: {
        automatic: true
        indexingMode: 'consistent'
        includedPaths: [{ path: '/*' }]
        excludedPaths: [{ path: '/json/?' }]
        compositeIndexes: [
          [
            {
              path: '/kind'
              order: 'ascending'
            }
            {
              path: '/id'
              order: 'ascending'
            }
          ]
        ]
      }
    }
  }
}
