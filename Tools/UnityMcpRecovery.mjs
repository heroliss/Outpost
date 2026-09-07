/**
 * 显式 MCP stdio 恢复客户端。仅在宿主通道关闭时连接同一官方 Unity MCP Server；
 * 所有 Unity 操作仍通过 tools/call 和服务端队列，不直连 Unity HTTP bridge。
 * 请求与原始响应落盘；不自动重试写操作，不修改个人配置，退出时关闭本次创建的服务进程。
 */
import fs from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";

const [serverRootArgument, requestFile, responseFile] = process.argv.slice(2);
if (!serverRootArgument || !requestFile || !responseFile)
    throw new Error("用法：node Tools/UnityMcpRecovery.mjs <server-root> <request.json> <response.json>");
const serverRoot = path.resolve(serverRootArgument);
const sdkRoot = path.join(serverRoot, "node_modules/@modelcontextprotocol/sdk/dist/esm");
const { Client } = await import(pathToFileURL(path.join(sdkRoot, "client/index.js")).href);
const { StdioClientTransport } = await import(pathToFileURL(path.join(sdkRoot, "client/stdio.js")).href);
const request = JSON.parse(await fs.readFile(requestFile, "utf8"));
if (!request.projectPath || !Number.isInteger(request.port) || !Array.isArray(request.calls) || !request.calls.length)
    throw new Error("请求必须包含 projectPath、port 和非空 calls。");
for (const call of request.calls) {
    if (!/^unity_[a-z_]+$/.test(call.name) || call.arguments?.port !== request.port)
        throw new Error("只允许带所选 port 的 unity_* 工具调用。");
}
const client = new Client({ name: "ssframework-explicit-recovery", version: "0.1.0" });
const transport = new StdioClientTransport({
    command: process.execPath,
    args: [path.join(serverRoot, "src/index.js")],
    cwd: serverRoot,
    env: { UNITY_MCP_COMPACT_TOOLS: "1" },
    stderr: "pipe",
});
// 只保留末段错误以诊断连接；不把子进程日志混入协议响应或打印环境变量。
let stderrTail = "";
transport.stderr?.on("data", chunk => { stderrTail = (stderrTail + chunk.toString()).slice(-4000); });
const evidence = { status: "running", projectPath: request.projectPath, port: request.port, responses: [] };
async function persist() {
    await fs.mkdir(path.dirname(path.resolve(responseFile)), { recursive: true });
    await fs.writeFile(responseFile, JSON.stringify(evidence, null, 2), "utf8");
}
function decode(result) {
    const block = result.content?.find(item => item.type === "text");
    if (!block) throw new Error("MCP 响应没有文本数据。");
    return JSON.parse(block.text);
}
try {
    await persist();
    await client.connect(transport);
    const inventory = await client.callTool({ name: "unity_list_instances", arguments: {} });
    evidence.inventory = inventory;
    const instances = decode(inventory).instances ?? [];
    const selected = instances.find(item => item.port === request.port);
    if (!selected || path.resolve(selected.projectPath).toLowerCase() !== path.resolve(request.projectPath).toLowerCase())
        throw new Error("所选端口不属于明确指定的项目；停止调用。");
    evidence.selection = await client.callTool({ name: "unity_select_instance", arguments: { port: request.port } });
    const selection = decode(evidence.selection);
    if (selection.success !== true || selection.instance?.port !== request.port ||
        path.resolve(selection.instance.projectPath).toLowerCase() !== path.resolve(request.projectPath).toLowerCase())
        throw new Error("选择 Unity 实例失败或项目身份已变化。");
    await persist();
    for (const call of request.calls) {
        const response = await client.callTool(call, undefined, { timeout: 120000 });
        evidence.responses.push({ request: call, response });
        await persist();
        const decoded = decode(response);
        if (response.isError || decoded.success === false || decoded.data?.success === false)
            throw new Error("工具返回失败：" + call.name);
    }
    evidence.status = "completed";
    await persist();
    process.stdout.write(JSON.stringify({ status: evidence.status, calls: evidence.responses.length, responseFile }) + "\n");
} catch (error) {
    evidence.status = "failed";
    evidence.error = String(error);
    evidence.stderrTail = stderrTail;
    await persist();
    process.exitCode = 1;
    process.stderr.write(String(error) + "\n");
} finally {
    // SDK 在对端意外退出后可能仍留有请求计时器；关闭自有 stdio 子进程后结束本次 CLI。
    // SDK transport 的关闭会先结束 stdin，再在 2/4 秒升级终止它自己创建的子进程。
    let closeTimer;
    const closed = await Promise.race([
        client.close().then(() => true),
        new Promise(resolve => { closeTimer = setTimeout(() => resolve(false), 8000); }),
    ]);
    clearTimeout(closeTimer);
    if (!closed) {
        evidence.closeTimedOut = true;
        evidence.status = "failed";
        await persist();
        process.exitCode = 1;
    }
    process.exit(process.exitCode ?? 0);
}
