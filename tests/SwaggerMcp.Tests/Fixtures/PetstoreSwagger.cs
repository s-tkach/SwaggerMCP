namespace SwaggerMcp.Tests.Fixtures;

internal static class PetstoreSwagger
{
    public const string Json = """
        {
          "openapi": "3.0.1",
          "info": {
            "title": "Petstore",
            "version": "v1"
          },
          "servers": [
            { "url": "https://petstore.local" }
          ],
          "paths": {
            "/pets": {
              "get": {
                "tags": ["pets"],
                "operationId": "listPets",
                "summary": "List pets",
                "parameters": [
                  {
                    "name": "limit",
                    "in": "query",
                    "schema": { "type": "integer" }
                  }
                ],
                "responses": {
                  "200": {
                    "description": "Pets returned",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "required": ["id", "name"],
                            "properties": {
                              "id": { "type": "integer" },
                              "name": { "type": "string" },
                              "status": { "type": "string", "enum": ["available", "sold"] }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "post": {
                "tags": ["pets"],
                "operationId": "createPet",
                "summary": "Create a pet",
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["name"],
                        "properties": {
                          "name": { "type": "string" },
                          "tag": { "type": "string" }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "201": {
                    "description": "Pet created"
                  }
                }
              }
            }
          }
        }
        """;
}
