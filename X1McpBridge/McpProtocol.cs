// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    internal static class McpProtocol
    {
        public const string ProtocolVersion = "2024-11-05";

        public static JObject Ok(object id, JToken result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id == null ? JValue.CreateNull() : JToken.FromObject(id),
                ["result"] = result
            };
        }

        public static JObject Err(object id, int code, string message, string data = null)
        {
            var err = new JObject
            {
                ["code"] = code,
                ["message"] = message
            };
            if (data != null)
                err["data"] = data;
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id == null ? JValue.CreateNull() : JToken.FromObject(id),
                ["error"] = err
            };
        }

        public static bool IsNotification(JObject msg)
        {
            return msg["id"] == null || msg["id"].Type == JTokenType.Null;
        }
    }
}
