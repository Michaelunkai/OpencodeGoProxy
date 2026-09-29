"use strict";
// Mock of the OpenCode Go gateway for the protocol-ownership test.
//   * Models that Go serves ONLY on the Responses API reject /chat/completions
//     with the real 400 ModelProtocolUnsupported body.
//   * /responses answers every model.
const http = require("http");
const fs = require("fs");
const LOG = process.env.MOCK_LOG || "mock.log";

function log(line) {
  try { fs.appendFileSync(LOG, new Date().toISOString() + " " + line + "\n"); } catch {}
}

const RESPONSES_ONLY = new Set(["muse-spark-1.3-contributor", "space-bunny-free", "grok-4.6"]);
let seq = 0;

const server = http.createServer((request, response) => {
  let body = "";
  request.on("data", (chunk) => (body += chunk));
  request.on("end", () => {
    let parsed = {};
    try { parsed = JSON.parse(body || "{}"); } catch {}
    const model = parsed.model || "";
    const wire = parsed.stream === true ? " (stream)" : "";
    log(request.method + " " + request.url + " model=" + model + wire);

    if (request.method === "GET" && request.url.endsWith("/models")) {
      const data = ["muse-spark-1.3-contributor", "space-bunny-free", "deepseek-v4-flash"]
        .map((id) => ({ id, object: "model" }));
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ object: "list", data }));
      return;
    }

    if (request.url.endsWith("/chat/completions")) {
      if (RESPONSES_ONLY.has(model)) {
        response.writeHead(400, { "content-type": "application/json" });
        response.end(JSON.stringify({
          type: "error",
          error: { type: "ModelProtocolUnsupported", message: "Model does not support this protocol." },
        }));
        return;
      }
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({
        id: "chatcmpl-mock", object: "chat.completion", created: 1, model,
        choices: [{ index: 0, message: { role: "assistant", content: "pong-from-chat" }, finish_reason: "stop" }],
        usage: { prompt_tokens: 1, completion_tokens: 2, total_tokens: 3 },
      }));
      return;
    }

    if (request.url.endsWith("/responses")) {
      seq += 1;
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({
        id: "resp_mock_" + seq, object: "response", created_at: 11, model, status: "completed",
        output: [{
          type: "message", id: "msg_mock", role: "assistant", status: "completed",
          content: [{ type: "output_text", text: "pong-from-responses", annotations: [] }],
        }],
        usage: { input_tokens: 3, output_tokens: 4, total_tokens: 7 },
      }));
      return;
    }

    response.writeHead(404, { "content-type": "application/json" });
    response.end("{}");
  });
});

server.listen(4098, "127.0.0.1", () => console.log("MOCK_READY"));
