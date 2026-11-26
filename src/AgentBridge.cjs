'use strict';

const { spawn, execFileSync } = require('node:child_process');
const { randomBytes, timingSafeEqual } = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const readline = require('node:readline');
const { setTimeout: delay } = require('node:timers/promises');

const ACP_COMMANDS = {
    ClaudeCode: { bin: 'claude-agent-acp', packages: ['@agentclientprotocol/claude-agent-acp', '@zed-industries/claude-agent-acp'], args: [] },
    GeminiCli: { bin: 'gemini', packages: ['@google/gemini-cli'], args: ['--acp'] },
    OpenCode: { bin: 'opencode', packages: ['opencode-ai', '@opencode/cli'], args: ['acp'] },
    QwenCode: { bin: 'qwen', packages: ['@qwen-code/qwen-code'], args: ['--acp'] }
};
const AGENTS = ['Codex', 'Dsh', 'Antigravity', 'Hermes', ...Object.keys(ACP_COMMANDS)];
const LABEL = { Codex: 'Codex', Dsh: 'DSH', Antigravity: 'Antigravity', Hermes: 'Hermes',
    ClaudeCode: 'Claude Code', GeminiCli: 'Gemini CLI', OpenCode: 'OpenCode', QwenCode: 'Qwen Code' };
const ALIASES = { Codex: ['GPT'], Dsh: ['DeepSeek', 'DeepSeek Harness'], Antigravity: ['Gemini'],
    ClaudeCode: ['Claude'], GeminiCli: ['GeminiCLI'], QwenCode: ['Qwen'] };
const AGENT_NAMES = AGENTS.flatMap(agent => [LABEL[agent], ...(ALIASES[agent] || [])]);
const ALIAS_GUIDE = 'Workspace collaboration names: ' + AGENTS.filter(agent => ALIASES[agent])
    .map(agent => `${ALIASES[agent].join(' / ')} → ${LABEL[agent]}`).join(', ') + '.';
const OPEN_TIMEOUT = 45000;
const TURN_TIMEOUT = 20 * 60 * 1000;
const FILE_HANDOFF = 'For file work, send the absolute path, the requested action, and only the necessary pointers (for example section or line numbers). Use a path the recipient can resolve, such as a mounted path for Hermes on WSL. Read the file locally if needed. Do not paste or retype an entire article, document, or source file into a peer request or reply. Return concise findings and the output path. If the peer cannot access the file, report the blocker and ask for a shared path.';
const safeError = error => String(error?.message || error || '未知错误').replace(/\u0000/g, '').replace(/\b(?:Bearer\s+\S+|sk-[A-Za-z0-9_-]{10,}|eyJ[^\s"']{25,})/gi, '[已隐藏]').slice(0, 900);
const parseLine = line => JSON.parse(line.replace(/^\ufeff/, ''));
const EFFORT_NAMES = { off: '关闭', none: '关闭', minimal: '最低', low: '低', medium: '中', high: '高', xhigh: '极高', max: '最高', ultra: '超高' };
const MODE_NAMES = { default: '默认', plan: '计划', 'accept-edits': '接受编辑', auto: '自动' };
const ACCESS_NAMES = { ':read-only': '只读', ':workspace': '工作区', ':danger-full-access': '完全访问' };
const SETTING_NAMES = { model: '模型', effort: '思考强度', mode: '模式', access: '文件访问', approval: '审批' };

function settingField(id, current, choices, native = id) {
    return { id, label: SETTING_NAMES[id], current, choices, native };
}

function acpChoices(options) {
    return (options || []).flatMap(option => Array.isArray(option.options) ? acpChoices(option.options) :
        typeof option.value === 'string' ? [{ value: option.value, label: option.name || option.value }] : []);
}

function resolveAgent(name) {
    if (typeof name !== 'string') return undefined;
    const key = name.trim().toLowerCase();
    return AGENTS.find(agent => [LABEL[agent], ...(ALIASES[agent] || [])].some(label => label.toLowerCase() === key));
}

function roleInstructions(agent, main) {
    const aliases = ALIAS_GUIDE + ' Each pane uses its own configured model. Hermes is a separate agent even when its model is DeepSeek.';
    if (agent === main) return [
        `You are ${LABEL[agent]}, the main agent in this Collaboration Chat workspace. Own the task and give the user the final response.`,
        aliases,
        'Use the local_collaboration MCP tools: list_agents gives the open peers, their aliases and busy status; ask_agent sends a focused request to a peer and returns its reply.',
        'When the user asks a named peer to contribute, actually call ask_agent once its prerequisites are ready. Respect required ordering and start independent requested reviews when ready. Use the workspace aliases for collaboration unless the user specifically asks for another interface. If a name refers to your own pane, do that part yourself.',
        FILE_HANDOFF,
        'Send only relevant context and a clear deliverable, never credentials or private reasoning. Each call can spend the peer’s quota. Use optional delegation only when it materially helps.',
        'Timestamped roster notices are automatic workspace updates. Follow the latest roster and continue the current task without replying to the notice.',
        'Track required reviews until they reply or report a blocker. If a peer is unavailable, busy, or the call fails, tell the user what is still pending. Report a peer review as complete only after its reply arrives. Verify consequential claims and distinguish reported work from verified results.',
        'On follow-up after an interruption, use the latest user goal and check which steps and reviews actually finished before repeating work.'
    ].join('\n');
    return [
        `You are ${LABEL[agent]}, an assisting agent in this Collaboration Chat workspace. ${LABEL[main]} is the main agent and coordinates peers.`,
        aliases,
        FILE_HANDOFF,
        'For a delegated request, complete the assigned scope and put findings, deliverables, and any blockers in your final public reply. Collaboration Chat returns that reply to the main agent automatically for MCP requests. For a direct user message, reply to the user in this pane.',
        'Keep other-agent requests as recommendations for the main agent to coordinate. Never report work or reviews that did not happen.'
    ].join('\n');
}

class RpcPeer {
    constructor(command, args, options, events, handleRequest, onExit) {
        this.nextId = 1;
        this.pending = new Map();
        this.child = spawn(command, args, { ...options, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
        this.child.stderr.on('data', () => {});
        this.child.stdin.on('error', () => {});
        const input = readline.createInterface({ input: this.child.stdout, crlfDelay: Infinity });
        input.on('line', raw => {
            let msg;
            try { msg = parseLine(raw); } catch { return; }
            if (!msg || typeof msg !== 'object') return;
            if (msg.method && msg.id != null) {
                Promise.resolve().then(() => handleRequest(msg.method, msg.params || {})).then(result => {
                    this.send({ id: msg.id, result });
                }, error => this.send({ id: msg.id, error: { code: -32601, message: safeError(error) } })).catch(() => {});
            } else if (msg.method) {
                events(msg.method, msg.params || {});
            } else if (this.pending.has(msg.id)) {
                const promise = this.pending.get(msg.id);
                this.pending.delete(msg.id);
                if (msg.error) promise.reject(Object.assign(new Error(safeError(msg.error)), { remote: true }));
                else promise.resolve(msg.result);
            }
        });
        const finish = error => {
            if (this.closed) return;
            this.closed = true;
            for (const promise of this.pending.values()) promise.reject(error);
            this.pending.clear();
            onExit(error);
        };
        this.child.once('error', finish);
        this.child.once('exit', (code, signal) => finish(new Error(`Agent 进程已退出 (${code ?? signal})`)));
    }

    send(packet) {
        if (!this.closed && !this.closing && this.child.stdin.writable) this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', ...packet }) + '\n');
    }

    call(method, params, timeout = OPEN_TIMEOUT) {
        if (this.closed || this.closing) return Promise.reject(new Error('Agent 连接已关闭。'));
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(`${method} 超时。`));
            }, timeout);
            this.pending.set(id, { resolve: value => { clearTimeout(timer); resolve(value); }, reject: error => { clearTimeout(timer); reject(error); } });
            this.send({ id, method, params });
        });
    }

    notify(method, params) { this.send({ method, params }); }

    close() {
        if (!this.closed && !this.closing) {
            this.closing = true;
            this.child.stdin.end();
            const child = this.child;
            setTimeout(() => { if (child.exitCode == null && child.signalCode == null) child.kill(); }, 1500).unref();
        }
    }
}

function isFile(file) {
    try { return typeof file === 'string' && path.isAbsolute(file) && fs.statSync(file).isFile(); }
    catch { return false; }
}

function installedPath(name, candidates = [], env = process.env) {
    const search = (env.PATH || env.Path || '').split(path.delimiter)
        .map(entry => entry.trim().replace(/^"|"$/g, '')).filter(entry => path.isAbsolute(entry))
        .map(entry => path.join(entry, name));
    return [...candidates, ...search].find(isFile);
}

function npmCommand(bin, packages, env) {
    const shim = installedPath(bin + '.cmd', [], env);
    const roots = [env.APPDATA && path.join(env.APPDATA, 'npm'), shim && path.dirname(shim)].filter(Boolean);
    for (const root of new Set(roots)) {
        for (const name of packages) {
            const directory = path.join(root, 'node_modules', name);
            try {
                const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'package.json'), 'utf8'));
                if (manifest.name !== name) continue;
                const entry = typeof manifest.bin === 'string' ? manifest.bin : manifest.bin?.[bin];
                if (typeof entry !== 'string') continue;
                const target = path.resolve(directory, entry);
                const relative = path.relative(directory, target);
                if (!relative || relative === '..' || relative.startsWith('..' + path.sep) ||
                    path.isAbsolute(relative) || !isFile(target)) continue;
                const descriptor = fs.openSync(target, 'r');
                try {
                    const header = Buffer.alloc(120);
                    fs.readSync(descriptor, header, 0, header.length, 0);
                    if (/\.(?:mjs|cjs|js)$/i.test(target) || /^#![^\r\n]*\bnode\b/.test(header.toString('utf8')))
                        return { command: process.execPath, args: [target] };
                    if (/\.exe$/i.test(target)) return { command: target, args: [] };
                } finally { fs.closeSync(descriptor); }
            } catch {}
        }
    }
}

function notInstalled(message) {
    return Object.assign(new Error(message), { code: 'NOT_INSTALLED' });
}

let detectedHermes;
function findHermes(env) {
    const wsl = installedPath('wsl.exe', [path.join(env.SystemRoot || 'C:\\Windows', 'System32', 'wsl.exe')], env);
    if (!wsl) throw notInstalled('找不到 WSL，请先按 Hermes 的官方教程安装。');
    if (detectedHermes) return detectedHermes;
    const run = args => {
        const output = execFileSync(wsl, args, { windowsHide: true, timeout: 4000, stdio: ['ignore', 'pipe', 'ignore'] });
        return output.toString(output.includes(0) ? 'utf16le' : 'utf8').replace(/^\ufeff/, '').trim();
    };
    let choices = [env.COLLAB_HERMES_DISTRO].filter(Boolean);
    if (!choices.length) {
        try { choices = run(['--list', '--quiet']).split(/\r?\n/).map(item => item.trim())
            .filter(item => item && !/^docker-desktop/i.test(item)); } catch {}
    }
    const requested = env.COLLAB_HERMES_CLI;
    if (requested && !/^\/[\S ]+$/.test(requested)) throw new Error('Hermes 启动路径须为完整 Linux 路径。');
    for (const distro of choices) {
        try {
            const prefix = ['-d', distro, '--exec'];
            let executable = requested;
            if (!executable) {
                try { executable = run([...prefix, '/usr/bin/which', 'hermes']); } catch {}
                if (!executable) {
                    const home = run([...prefix, '/usr/bin/printenv', 'HOME']);
                    if (!home.startsWith('/') || /[\r\n\u0000]/.test(home)) continue;
                    executable = home.replace(/\/$/, '') + '/.local/bin/hermes';
                }
            }
            if (!executable.startsWith('/') || /[\r\n\u0000]/.test(executable)) continue;
            run([...prefix, '/usr/bin/test', '-x', executable]);
            return detectedHermes = { command: wsl, distro, executable };
        } catch {}
    }
    throw notInstalled('WSL 中未检测到 Hermes Agent。');
}

function codexPath(env) {
    const standalone = installedPath('codex.exe', [env.COLLAB_CODEX, env.CODEX_CLI_PATH]);
    if (standalone) return standalone;

    const powershell = path.join(env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    const locatePackage = '[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); '
        + 'Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | ForEach-Object { $_.InstallLocation }';
    try {
        const locations = execFileSync(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', locatePackage], {
            encoding: 'utf8', windowsHide: true, timeout: 8000, stdio: ['ignore', 'pipe', 'ignore']
        });
        for (const directory of locations.split(/\r?\n/).map(line => line.replace(/^\ufeff/, '').trim())) {
            if (!path.isAbsolute(directory)) continue;
            const bundled = path.join(directory, 'app', 'resources', 'codex.exe');
            if (fs.existsSync(bundled)) return bundled;
        }
    } catch { return undefined; }
    return undefined;
}

function windowsToWsl(file, distribution) {
    const drive = /^([A-Za-z]):[\\/](.*)$/.exec(file);
    if (drive) return `/mnt/${drive[1].toLowerCase()}/${drive[2].replace(/\\/g, '/')}`;
    const unc = /^\\\\(?:wsl\.localhost|wsl\$)\\([^\\]+)(.*)$/i.exec(file);
    if (unc && unc[1].toLowerCase() === distribution.toLowerCase()) return unc[2].replace(/\\/g, '/') || '/';
    throw new Error('Hermes 需要本机磁盘目录，或其 WSL 发行版中的目录。');
}

function recipe(agent, workspace, mcp) {
    const env = { ...process.env };
    const directory = workspace.directory;
    const local = env.LOCALAPPDATA || '';
    const app = env.APPDATA || '';
    if (agent === 'Codex') {
        const command = codexPath(env);
        if (!command) throw notInstalled('找不到本机 Codex CLI。已检查 PATH 和已注册的 Codex 桌面安装。');
        const args = ['app-server'];
        if (mcp) args.push('-c', `mcp_servers.local_collaboration.command=${JSON.stringify(mcp.command)}`,
            '-c', `mcp_servers.local_collaboration.args=${JSON.stringify(mcp.args)}`,
            '-c', 'mcp_servers.local_collaboration.default_tools_approval_mode="approve"',
            '-c', 'mcp_servers.local_collaboration.tool_timeout_sec=1200');
        return { protocol: 'codex', command, args, cwd: directory, env };
    }
    if (agent === 'Dsh') {
        const entry = env.COLLAB_DSH || path.join(app, 'npm', 'node_modules', '@deepseek-ai', 'dsh', 'lib', 'bin.js');
        if (!isFile(entry)) throw notInstalled('找不到 DeepSeek Harness CLI 的 ACP 入口。');
        return { protocol: 'acp', command: process.execPath, args: [entry, '--profile', 'acp'], cwd: directory, env };
    }
    if (agent === 'Antigravity') {
        const command = installedPath('agy.exe', [env.COLLAB_AGY, path.join(local, 'agy', 'bin', 'agy.exe')]);
        if (!command) throw notInstalled('找不到官方 Antigravity CLI（agy）。');
        return { protocol: 'acp', command, args: ['acp'], cwd: directory, env };
    }
    if (agent === 'Hermes') {
        const { command, distro, executable } = findHermes(env);
        return { protocol: 'acp', command, args: ['-d', distro, '--exec', executable, 'acp'], cwd: directory,
            agentDirectory: windowsToWsl(directory, distro), wsl: distro, env };
    }
    const adapter = ACP_COMMANDS[agent];
    if (adapter) {
        const exe = installedPath(adapter.bin + '.exe', [], env);
        const launch = exe ? { command: exe, args: [] } : npmCommand(adapter.bin, adapter.packages, env);
        if (!launch) {
            const hasClaude = agent === 'ClaudeCode' && (installedPath('claude.exe', [], env) || npmCommand('claude', ['@anthropic-ai/claude-code'], env));
            if (hasClaude) throw Object.assign(new Error('已检测到 Claude Code，仍需安装 Claude Agent ACP。'), { code: 'MISSING_ADAPTER' });
            throw notInstalled(`找不到 ${LABEL[agent]} 的原生 ACP 入口。`);
        }
        return { protocol: 'acp', command: launch.command, args: [...launch.args, ...adapter.args], cwd: directory, env };
    }
    throw new Error('不支持的 Agent。');
}

function scanInstallations() {
    return AGENTS.map(agent => {
        try {
            recipe(agent, { directory: process.cwd() }, null);
            return { agent, status: 'installed', detail: '已检测到 ACP 或原生 CLI 入口。' };
        } catch (error) {
            return { agent, status: error.code === 'MISSING_ADAPTER' ? 'adapter' : error.code === 'NOT_INSTALLED' ? 'missing' : 'unknown',
                detail: safeError(error) };
        }
    });
}

function detectInstallations(emit, id) {
    const child = spawn(process.execPath, [__filename, 'detect'], { windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] });
    let output = '';
    let failed;
    const timer = setTimeout(() => child.kill(), 32000);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => {
        output += chunk;
        if (output.length > 30000) child.kill();
    });
    child.once('error', error => {
        failed = error;
    });
    child.once('close', code => {
        clearTimeout(timer);
        try {
            if (failed) throw failed;
            if (code !== 0) throw new Error('安装检测未完成，请重试。');
            emit({ type: 'installationResult', run: id, results: JSON.parse(output) });
        } catch (error) { emit({ type: 'installationResult', run: id, text: safeError(error) }); }
    });
}

class AgentSession {
    constructor(agent, workspace, handler, mcp, resolver = recipe) {
        this.agent = agent;
        this.workspace = workspace;
        this.handler = handler;
        this.mcp = mcp;
        this.definition = resolver(agent, workspace, mcp);
    }

    async connect(oldSessionId) {
        if (this.peer && !this.peer.closed && !this.peer.closing && this.sessionId) {
            if (oldSessionId && this.sessionId !== oldSessionId) throw new Error('连接的会话与保存的会话编号不一致。');
            return;
        }
        const spec = this.definition;
        this.peer = new RpcPeer(spec.command, spec.args, { cwd: spec.cwd, env: spec.env },
            (method, params) => this.event(method, params),
            (method, params) => this.serverRequest(method, params),
            error => this.turn?.reject?.(error));
        try {
            this.sessionId = spec.protocol === 'codex' ? await this.openCodex(oldSessionId) : await this.openAcp(oldSessionId);
            if (!this.sessionId) throw new Error('Agent 没有返回会话编号。');
            if (oldSessionId && this.sessionId !== oldSessionId) throw new Error('Agent 返回了不同的会话编号。');
            this.sessionPersistent = !!oldSessionId || spec.protocol !== 'codex';
            this.handler('session', { sessionId: this.sessionId });
        } catch (error) {
            this.close();
            throw oldSessionId ? new Error('恢复原会话失败，已保留会话编号。' + safeError(error)) : error;
        }
    }

    async ensurePersistent() {
        if (this.sessionPersistent) return;
        // A fresh Codex thread is only reloadable after an item reaches its rollout.
        await this.peer.call('thread/inject_items', {
            threadId: this.sessionId,
            items: [{ type: 'message', role: 'developer', content: [{ type: 'input_text',
                text: 'This is a persistent Collaboration Chat thread. Keep the current conversation context in subsequent turns.' }] }]
        });
        this.sessionPersistent = true;
    }

    async openCodex(oldSessionId) {
        await this.peer.call('initialize', { clientInfo: { name: 'relaypane', version: '1.0.0' }, capabilities: { experimentalApi: true } });
        this.peer.notify('initialized', {});
        const opts = { cwd: this.workspace.directory, approvalPolicy: 'never', developerInstructions: roleInstructions(this.agent, this.workspace.main) };
        const result = oldSessionId
            ? await this.peer.call('thread/resume', { ...opts, threadId: oldSessionId, excludeTurns: true })
            : await this.peer.call('thread/start', { ...opts, serviceName: 'relaypane' });
        this.codexDefaults = result;
        return result.thread?.id;
    }

    async checkConnection() {
        if (this.definition.protocol !== 'codex') return '官方 ACP 会话已连接。';
        const result = await this.peer.call('account/read', { refreshToken: false });
        if (result.requiresOpenaiAuth && !result.account) throw new Error('Codex 尚未登录。请先在官方 Codex 中登录。');
        return result.account ? '官方 App Server 已连接，已读到本机登录状态。' : '官方 App Server 已连接。';
    }

    async openAcp(oldSessionId) {
        const capabilities = await this.peer.call('initialize', { protocolVersion: 1, clientCapabilities: {},
            clientInfo: { name: 'relaypane', version: '1.0.0' } });
        const mcp = this.mcp && { ...this.mcp };
        if (mcp && this.definition.wsl) mcp.command = windowsToWsl(mcp.command, this.definition.wsl);
        const params = { cwd: this.definition.agentDirectory || this.workspace.directory, mcpServers: mcp ? [mcp] : [] };
        if (oldSessionId) {
            const features = capabilities?.agentCapabilities || {};
            const method = features.sessionCapabilities?.resume ? 'session/resume' : features.loadSession ? 'session/load' : null;
            if (!method) throw new Error('此版本无法恢复保存的 ACP 会话。');
            const resumed = await this.peer.call(method, { ...params, sessionId: oldSessionId });
            this.rememberAcpSettings(resumed);
            return resumed?.sessionId || oldSessionId;
        }
        const opened = await this.peer.call('session/new', params);
        this.rememberAcpSettings(opened);
        return opened?.sessionId;
    }

    rememberAcpSettings(result) {
        if (Array.isArray(result?.configOptions)) this.acpSettings = result.configOptions;
        if (result?.modes) this.acpModes = result.modes;
        if (result?.models) this.acpModels = result.models;
    }

    async codexCatalog(refresh) {
        if (!refresh && this.catalog) return this.catalog;
        const list = (method, params = {}) => this.peer.call(method, params).then(result => result.data || []);
        const [models, modes, access] = await Promise.all([
            list('model/list', { limit: 100, ...(this.codexDefaults?.modelProvider ? { modelProvider: this.codexDefaults.modelProvider } : {}) }),
            list('collaborationMode/list').catch(() => []),
            list('permissionProfile/list', { cwd: this.workspace.directory, limit: 100 }).catch(() => [])
        ]);
        this.catalog = { models, modes, access };
        return this.catalog;
    }

    async settingsFields(preferences = {}, refresh = false) {
        if (this.agent === 'Hermes') return [];
        if (this.definition.protocol === 'codex') {
            const { models, modes, access } = await this.codexCatalog(refresh);
            const defaults = this.codexDefaults || {};
            const activeModel = preferences.model || defaults.model || models.find(item => item.isDefault)?.model;
            const model = models.find(item => (item.model || item.id) === activeModel);
            const fields = [
                settingField('model', activeModel, models.map(item => ({ value: item.model || item.id, label: item.displayName || item.model || item.id }))),
                settingField('effort', preferences.effort || (preferences.model ? model?.defaultReasoningEffort : defaults.reasoningEffort) || model?.defaultReasoningEffort,
                    (model?.supportedReasoningEfforts || []).map(item => ({ value: item.reasoningEffort, label: EFFORT_NAMES[item.reasoningEffort] || item.reasoningEffort }))),
                settingField('mode', preferences.mode || defaults.collaborationMode?.mode || 'default',
                    modes.filter(item => item.mode && item.name).map(item => ({ value: item.mode, label: MODE_NAMES[item.mode] || item.name }))),
                settingField('access', preferences.access || defaults.activePermissionProfile?.id ||
                    ({ readOnly: ':read-only', workspaceWrite: ':workspace', dangerFullAccess: ':danger-full-access' })[defaults.sandbox?.type],
                    access.filter(profile => profile.allowed === true).map(profile => ({ value: profile.id, label: ACCESS_NAMES[profile.id] || profile.name || profile.id }))),
                settingField('approval', preferences.approval || 'never',
                    [{ value: 'never', label: '不弹审阅' }, { value: 'on-request', label: '按需确认' }])
            ];
            return fields.filter(field => field.choices.length);
        }
        const fields = [];
        for (const option of this.acpSettings || []) {
            const category = option.category || option.id;
            const id = ['thought_level', 'reasoning_effort'].includes(category) ? 'effort' : category;
            if (!['model', 'effort', 'mode'].includes(id) || option.type !== 'select') continue;
            const translations = id === 'effort' ? EFFORT_NAMES : id === 'mode' ? MODE_NAMES : {};
            const choices = acpChoices(option.options).map(choice => ({ ...choice, label: translations[choice.value] || choice.label }));
            if (choices.length) fields.push(settingField(id, option.currentValue, choices, option.id));
        }
        if (this.acpModels?.availableModels?.length && !fields.some(field => field.id === 'model'))
            fields.push(settingField('model', this.acpModels.currentModelId,
                this.acpModels.availableModels.map(model => ({ value: model.modelId, label: model.name })), 'session/set_model'));
        if (this.acpModes?.availableModes?.length && !fields.some(field => field.id === 'mode'))
            fields.push(settingField('mode', this.acpModes.currentModeId,
                this.acpModes.availableModes.map(mode => ({ value: mode.id, label: MODE_NAMES[mode.id] || mode.name })), 'session/set_mode'));
        return fields;
    }

    async applySettings(preferences, refresh = false) {
        let fields = await this.settingsFields(preferences, refresh);
        for (const id of Object.keys(SETTING_NAMES)) {
            if (!preferences[id]) continue;
            const field = fields.find(item => item.id === id);
            if (!field?.choices.some(choice => choice.value === preferences[id]))
                throw new Error(`${SETTING_NAMES[id]}的原保存选项已不可用，请在 Agent 设置中重新选择。`);
            if (this.definition.protocol === 'codex' || field.current === preferences[id]) continue;
            const value = preferences[id];
            const sessionId = this.sessionId;
            if (field.native === 'session/set_model') {
                await this.peer.call(field.native, { sessionId, modelId: value });
                this.acpModels.currentModelId = value;
            } else if (field.native === 'session/set_mode') {
                await this.peer.call(field.native, { sessionId, modeId: value });
                this.acpModes.currentModeId = value;
            } else {
                const updated = await this.peer.call('session/set_config_option', { sessionId, configId: field.native, value });
                this.rememberAcpSettings(updated);
                if (this.acpSettings?.find(option => option.id === field.native)?.currentValue !== value)
                    throw new Error(`Agent 没有确认新的${SETTING_NAMES[id]}。`);
            }
            fields = await this.settingsFields();
        }
        return fields;
    }

    async prepareSettings(preferences) {
        if (this.agent === 'Hermes' || !preferences || !Object.keys(preferences).length)
            return this.definition.protocol === 'codex' ? { approvalPolicy: 'never' } : {};
        const fields = await this.applySettings(preferences);
        if (this.definition.protocol !== 'codex') return {};
        const current = id => fields.find(field => field.id === id)?.current;
        const effectiveModel = preferences.model || current('model');
        const effort = preferences.effort || current('effort');
        const options = {
            ...(preferences.model ? { model: preferences.model } : {}),
            ...(preferences.effort || preferences.model && effort ? { effort } : {}),
            ...(preferences.access ? { permissions: preferences.access } : {}),
            approvalPolicy: preferences.approval || 'never'
        };
        if (preferences.mode) {
            if (!effectiveModel) throw new Error('请先选择 Codex 模型，再切换模式。');
            options.collaborationMode = { mode: preferences.mode, settings: { model: effectiveModel,
                reasoning_effort: effort || null, developer_instructions: null } };
        }
        return options;
    }

    async prompt(text, origin, roster, options = {}) {
        if (this.turn) throw new Error('此 Agent 正在回复。');
        const turn = { text: '', item: null, cancelled: false, messages: [], toolEpoch: 0, options };
        const codex = this.definition.protocol === 'codex';
        const context = codex && !options.collaborationMode ? [] : [roleInstructions(this.agent, this.workspace.main)];
        if (roster) context.push(roster);
        if (origin?.source) context.push(origin.manual
            ? `The user sent the following request directly using @${LABEL[this.agent]}. Reply in this pane for the user.`
            : `The following request was delegated by the main agent, ${LABEL[origin.source]}. Your final public reply is the result of its ask_agent call.`);
        // Keep the original input in its own block so its Markdown and whitespace are preserved.
        const input = context.length ? [{ type: 'text', text: `[Collaboration Chat context]\n${context.join('\n')}\n[End Collaboration Chat context]\n\n` }] : [];
        input.push({ type: 'text', text });
        this.turn = turn;
        try {
            if (codex) await this.promptCodex(input, turn);
            else {
                const response = await this.peer.call('session/prompt', { sessionId: this.sessionId, prompt: input }, TURN_TIMEOUT);
                this.acceptTurn(turn);
                if (response?.stopReason !== 'end_turn') throw new Error(response?.stopReason === 'cancelled' ? '已停止。' : `Agent 本轮结束状态：${response?.stopReason || '未知'}`);
            }
            if (turn.cancelled) throw new Error('已停止。');
            const messages = turn.messages.filter(message => message.text.trim() &&
                !(message.phase === 'commentary' && /^::browser-complete\{title="[^"\r\n]*"\}$/.test(message.text.trim())));
            const lastToolEpoch = messages.at(-1)?.toolEpoch;
            const final = codex
                ? [messages.findLast(message => message.phase === 'final_answer') || messages.findLast(message => !message.phase)].filter(Boolean)
                : messages.filter(message => message.toolEpoch === lastToolEpoch);
            if (!final.length && turn.messages.length) throw new Error('Agent 未给出最终回复。');
            const reply = final.length ? final.map(message => message.text).join('\n\n') : turn.text;
            if (!reply.trim()) throw new Error('Agent 未返回正文。');
            this.handler('presentation', { progress: messages.filter(message => !final.includes(message)).map(message => message.text).join('\n\n') });
            return reply;
        } finally {
            this.turn = null;
        }
    }

    acceptTurn(turn) {
        if (turn.accepted) return;
        turn.accepted = true;
        this.handler('accepted', { sessionId: this.sessionId });
    }

    async promptCodex(input, turn) {
        let timer;
        const finished = new Promise((resolve, reject) => {
            turn.resolve = resolve;
            turn.reject = reject;
            timer = setTimeout(() => reject(new Error('Codex 回复超时。')), TURN_TIMEOUT);
        });
        finished.catch(() => {});
        try {
            const started = await this.peer.call('turn/start', { threadId: this.sessionId,
                input: input.map(block => ({ ...block, text_elements: [] })), ...turn.options });
            turn.id = started.turn?.id;
            if (turn.id) {
                const { model, effort, permissions, approvalPolicy, collaborationMode } = turn.options;
                this.codexDefaults = { ...this.codexDefaults,
                    ...(model ? { model } : {}),
                    ...(effort ? { reasoningEffort: effort } : {}),
                    ...(permissions ? { activePermissionProfile: { id: permissions } } : {}),
                    ...(approvalPolicy ? { approvalPolicy } : {}),
                    ...(collaborationMode ? { collaborationMode } : {})
                };
                this.acceptTurn(turn);
            }
            if (turn.cancelled && turn.id) await this.peer.call('turn/interrupt', { threadId: this.sessionId, turnId: turn.id });
            await finished;
        } finally { clearTimeout(timer); }
    }

    async steer(text) {
        const current = this.turn;
        if (this.definition.protocol !== 'codex' || !current?.id || current.cancelled) return false;
        try {
            await this.peer.call('turn/steer', { threadId: this.sessionId, expectedTurnId: current.id,
                input: [{ type: 'text', text, text_elements: [] }] }, 10000);
            return true;
        } catch (error) {
            if (!error.remote) throw error;
            return false;
        }
    }

    event(method, params) {
        if (this.definition.protocol === 'acp' && method === 'session/update' &&
            (!this.sessionId || params.sessionId === this.sessionId) && params.update?.sessionUpdate === 'config_option_update')
            this.rememberAcpSettings(params.update);
        const turn = this.turn;
        if (!turn) return;
        if (this.definition.protocol === 'acp') {
            if (method !== 'session/update' || params.sessionId !== this.sessionId) return;
            this.acceptTurn(turn);
            const item = params.update;
            if (item?.sessionUpdate === 'tool_call') {
                turn.toolEpoch++;
                turn.acpBlock = null;
            }
            if (item?.sessionUpdate === 'agent_message_chunk' && item.content?.type === 'text' && typeof item.content.text === 'string') {
                if (!turn.acpBlock || (item.messageId != null && turn.acpBlock.id !== item.messageId)) {
                    turn.acpBlock = { id: item.messageId, text: '', toolEpoch: turn.toolEpoch };
                    turn.messages.push(turn.acpBlock);
                }
                turn.acpBlock.text += item.content.text;
                turn.text = turn.messages.map(message => message.text).join('\n\n');
                this.handler('reply', { text: turn.text });
            }
        } else {
            if (params.threadId !== this.sessionId || (turn.id && params.turnId && params.turnId !== turn.id)) return;
            if (method === 'item/agentMessage/delta' && typeof params.delta === 'string') {
                if (turn.item !== params.itemId) { turn.item = params.itemId; turn.itemStart = turn.text.length; if (turn.text) turn.text += '\n\n'; }
                turn.text += params.delta;
                this.handler('reply', { text: turn.text });
            }
            if (method === 'item/completed' && params.item?.type === 'agentMessage' && typeof params.item.text === 'string') {
                const item = params.item;
                turn.messages.push({ phase: item.phase, text: item.text });
                if (turn.item === item.id) turn.text = turn.text.slice(0, turn.itemStart) + (turn.itemStart ? '\n\n' : '') + item.text;
                else turn.text += (turn.text ? '\n\n' : '') + item.text;
                this.handler('reply', { text: turn.text });
            }
            if (method === 'turn/completed' && (!turn.id || params.turn?.id === turn.id)) {
                if (params.turn?.status === 'completed') turn.resolve?.();
                else turn.reject?.(new Error(params.turn?.error?.message || 'Codex 本轮未完成。'));
            }
        }
    }

    async serverRequest(method, params) {
        if (!this.turn) throw new Error('没有对应的活动会话。');
        const active = !this.turn.cancelled;
        if (method === 'session/request_permission') {
            const allowed = params.sessionId === this.sessionId && active &&
                (params.options?.find(option => option.kind === 'allow_once') || params.options?.find(option => option.kind === 'allow_always'));
            const denied = params.options?.find(option => option.kind === 'reject_once');
            const option = allowed || denied;
            return { outcome: option ? { outcome: 'selected', optionId: option.optionId } : { outcome: 'cancelled' } };
        }
        const codexActive = active && params.threadId === this.sessionId &&
            (!params.turnId || !this.turn.id || params.turnId === this.turn.id);
        const asks = this.turn.options?.approvalPolicy === 'on-request';
        if (method === 'item/commandExecution/requestApproval' || method === 'item/fileChange/requestApproval') {
            const allow = codexActive && (!asks || await this.handler('approval', {
                text: safeError(params.command || params.reason || 'Codex 请求执行操作'),
                detail: [params.cwd, params.grantRoot, params.networkApprovalContext?.host].filter(Boolean).join('\n')
            }));
            return { decision: allow ? 'accept' : 'decline' };
        }
        if (method === 'item/permissions/requestApproval') {
            const requested = params.permissions;
            const allow = codexActive && requested && typeof requested === 'object' && !Array.isArray(requested) &&
                (!asks || await this.handler('approval', { text: safeError(params.reason || 'Codex 请求额外权限'),
                    detail: JSON.stringify(requested, null, 2).slice(0, 1800) }));
            return { permissions: allow ? requested : {}, scope: 'turn' };
        }
        if (method === 'mcpServer/elicitation/request') return { action: 'decline', content: null };
        throw new Error('此原型不支持该 Agent 请求，请在对话里直接说明需要用户提供的信息。');
    }

    stop() {
        if (!this.turn) return;
        const active = this.turn;
        active.cancelled = true;
        if (this.definition.protocol === 'acp') this.peer.notify('session/cancel', { sessionId: this.sessionId });
        else if (this.turn.id) this.peer.call('turn/interrupt', { threadId: this.sessionId, turnId: this.turn.id }).catch(() => {});
        setTimeout(() => { if (this.turn === active) this.close(); }, 2500).unref();
    }

    close() { this.peer?.close(); }
}

class Coordinator {
    constructor(emit, endpoint, makeAgent = (...args) => new AgentSession(...args)) {
        this.emit = emit;
        this.endpoint = endpoint;
        this.makeAgent = makeAgent;
        this.rooms = new Map();
        this.approvals = new Map();
        this.bindingAcks = new Map();
    }

    room(settings) {
        if (!/^[a-f0-9]{32}$/.test(settings.id) || !AGENTS.includes(settings.main) || !path.isAbsolute(settings.directory)) throw new Error('工作区信息无效。');
        if (!fs.statSync(settings.directory).isDirectory()) throw new Error('项目目录不存在。');
        let room = this.rooms.get(settings.id);
        if (!room) this.rooms.set(settings.id, room = { ...settings, sessions: new Map(), connecting: new Map(), turns: new Map(),
            queued: new Map(), flushing: new Set(), configuring: new Set(), sessionIds: { ...settings.sessionIds },
            unconfirmedBindings: new Set(),
            preferences: { ...settings.preferences } });
        if (room.directory !== settings.directory || room.main !== settings.main) {
            if (room.turns.size || room.connecting.size || room.configuring.size) throw new Error('请等当前任务、连接或设置更新结束，再变更项目或主 Agent。');
            if (Object.values(room.sessionIds).some(Boolean)) throw new Error('当前对话已绑定原生会话，请在新对话中更改项目或主 Agent。');
            for (const session of room.sessions.values()) session.close();
            room.sessions.clear();
            room.sessionIds = { ...settings.sessionIds };
            room.preferences = { ...settings.preferences };
        }
        room.directory = settings.directory;
        room.main = settings.main;
        room.agents = settings.agents.filter(agent => AGENTS.includes(agent));
        room.confirmBinding = settings.confirmBinding === true;
        room.missingBindings = Array.isArray(settings.missingBindings) ? settings.missingBindings : [];
        room.rosterNotice = typeof settings.rosterNotice === 'string' ? settings.rosterNotice.slice(0, 2000) : '';
        return room;
    }

    sync(settings) {
        const room = this.room(settings);
        void this.flush(room, room.main);
    }

    async fromUser(packet) {
        try {
            const room = this.room(packet.workspace);
            const { agent, text } = packet;
            if (!packet.target && (room.turns.has(agent) || room.queued.has(agent)) && room.agents.includes(agent) &&
                typeof text === 'string' && text.trim() && text.length <= 12000) {
                const queue = room.queued.get(agent) || [];
                if (queue.length >= 12) {
                    this.emit({ type: 'submissionError', workspace: room.id, agent, text: '排队消息已达 12 条。', draft: text });
                    return;
                }
                room.queued.set(agent, queue);
                queue.push({ ...packet, queued: true });
                this.emit({ type: 'queued', workspace: room.id, agent, text, remaining: queue.length });
                void this.flush(room, agent);
            } else {
                await this.dispatch(room, packet);
            }
        } catch (error) {
            this.reportError(packet, error);
        }
    }

    async dispatch(room, packet) {
        try {
            if (packet.target) await this.delegate(room, room.main, packet.target, packet.text, true, packet.id);
            else await this.run(room, packet.agent, packet.text, packet.id, undefined, packet.queued);
        } catch (error) { this.reportError(packet, error); }
    }

    reportError(packet, error) {
        if (!error.handled) this.emit({ type: packet.queued ? 'submissionError' : 'error', workspace: packet.workspace?.id,
            agent: packet.target || packet.agent, run: packet.id, text: safeError(error) });
    }

    roster(room) {
        const displayName = agent => ALIASES[agent]?.length ? `${LABEL[agent]}（${ALIASES[agent][0]}）` : LABEL[agent];
        const peers = room.agents.filter(agent => agent !== room.main).map(displayName);
        const summary = `用户选择了 ${displayName(room.main)} 作为主 Agent。当前辅助 Agent：${peers.join('、') || '无'}。`;
        return room.rosterNotice ? summary + '\n\n' + room.rosterNotice : summary;
    }

    async flush(room, agent) {
        if (room.flushing.has(agent)) return;
        room.flushing.add(agent);
        try {
            while (true) {
                const queue = room.queued.get(agent);
                const active = room.turns.get(agent);
                if (!active) {
                    if (queue?.length) {
                        const next = queue.shift();
                        if (!queue.length) room.queued.delete(agent);
                        void this.dispatch(room, next);
                    }
                    return;
                }
                const session = room.sessions.get(agent);
                if (!active.ready || active.cancelled || active.source || agent !== 'Codex' || !session?.steer) return;
                const notice = agent === room.main ? this.roster(room) : null;
                if (notice && notice !== active.roster && notice !== active.rosterAttempt) {
                    active.rosterAttempt = notice;
                    try {
                        let accepted = await session.steer(notice);
                        if (!accepted) {
                            await delay(180);
                            if (room.turns.get(agent) === active && !active.cancelled && notice === this.roster(room))
                                accepted = await session.steer(notice);
                        }
                        if (accepted) active.roster = notice;
                    } catch { /* Delivery may be uncertain; the next native prompt carries the latest roster. */ }
                    continue;
                }
                if (!queue?.length) return;
                const next = queue[0];
                if (next.triedTurn === active.id) return;
                next.triedTurn = active.id;
                let sent;
                try { sent = await session.steer(next.text); }
                catch {
                    sent = null; // A transport failure can occur after the native agent receives the message.
                }
                if (room.queued.get(agent) !== queue) continue;
                if (sent !== false) {
                    queue.shift();
                    if (!queue.length) room.queued.delete(agent);
                    if (sent === true) this.emit({ type: 'steered', workspace: room.id, agent, remaining: queue.length });
                    else this.emit({ type: 'submissionError', workspace: room.id, agent, remaining: queue.length,
                        text: '插话是否送达无法确认，请查看当前回复。' });
                } else if (room.turns.get(agent) === active) return;
            }
        } finally {
            room.flushing.delete(agent);
        }
    }

    openSession(room, agent) {
        if (!room.sessionIds[agent] && room.missingBindings.includes(agent))
            return Promise.reject(new Error('历史里缺少这个 Agent 的原会话编号，无法恢复。'));
        if (room.connecting.has(agent)) return room.connecting.get(agent);
        const opening = Promise.resolve().then(async () => {
            let session = room.sessions.get(agent);
            try {
                if (!session || session.peer?.closed || session.peer?.closing) {
                    const mcp = agent === room.main ? { name: 'local_collaboration', command: process.execPath,
                        args: [__filename, 'mcp', this.endpoint, room.id, agent], env: [] } : null;
                    session = this.makeAgent(agent, room, (type, payload) => {
                        const active = room.turns.get(agent);
                        if (type === 'session') return;
                        if (type === 'accepted') {
                            if (active) {
                                active.ready = true;
                                void this.flush(room, agent);
                            }
                            return;
                        }
                        if (type === 'approval') return active && !active.cancelled ? this.askApproval(room, agent, active, payload) : false;
                        if (type === 'presentation') {
                            if (active) active.progress = payload.progress;
                            return;
                        }
                        if (active) this.emit({ ...payload, type, workspace: room.id, agent, run: active.id, source: active.source });
                    }, mcp);
                    room.sessions.set(agent, session);
                }
                await session.connect(room.sessionIds[agent]);
                return session;
            } catch (error) {
                session?.close();
                room.sessions.delete(agent);
                throw error;
            }
        }).finally(() => room.connecting.delete(agent));
        room.connecting.set(agent, opening);
        return opening;
    }

    async probe(packet) {
        const workspace = packet.workspace?.id;
        const run = packet.id;
        const finish = (agent, ok, text, notInstalled) => this.emit({ type: 'connectionResult', workspace, run, agent, ok, text, notInstalled });
        let room;
        try {
            room = this.room(packet.workspace);
        } catch (error) {
            for (const agent of Array.isArray(packet.workspace?.agents) ? packet.workspace.agents : []) finish(agent, false, safeError(error));
            return;
        }

        await Promise.all([...new Set(room.agents)].map(async agent => {
            try {
                if (room.turns.has(agent) || room.configuring.has(agent)) throw new Error(`${LABEL[agent]} 正在处理请求，请稍等。`);
                const session = await this.openSession(room, agent);
                finish(agent, true, await session.checkConnection());
            } catch (error) { finish(agent, false, safeError(error), error.code === 'NOT_INSTALLED'); }
        }));
    }

    async agentSettings(packet) {
        const { agent, id, change } = packet;
        const finish = payload => this.emit({ type: 'agentSettings', workspace: packet.workspace?.id, agent, run: id, ...payload });
        let room;
        let session;
        let reserved = false;
        try {
            room = this.room(packet.workspace);
            if (!room.agents.includes(agent) || agent === 'Hermes') throw new Error('该聊天框使用原生默认设置。');
            if (room.turns.has(agent) || room.queued.has(agent) || room.flushing.has(agent) || room.configuring.has(agent))
                throw new Error('请在该 Agent 空闲时更改设置。');
            room.configuring.add(agent);
            reserved = true;
            session = await this.openSession(room, agent);
            const preferences = { ...room.preferences[agent] };
            if (change) {
                if (change.id === 'clear') {
                    for (const key of Object.keys(preferences)) delete preferences[key];
                } else {
                    if (!Object.hasOwn(SETTING_NAMES, change.id) || typeof change.value !== 'string' || !change.value || change.value.length > 500)
                        throw new Error('Agent 设置无效。');
                    if (change.id === 'model' && preferences.model !== change.value) delete preferences.effort;
                    preferences[change.id] = change.value;
                }
            }
            const fields = await session.applySettings(preferences, !change);
            if (change) room.preferences[agent] = preferences;
            finish({ ok: true, fields, preferences: change ? preferences : undefined });
        } catch (error) {
            const fields = session ? await session.settingsFields().catch(() => []) : [];
            finish({ ok: false, text: safeError(error), fields });
        } finally {
            if (reserved) room.configuring.delete(agent);
        }
    }

    async bindSession(room, agent, active, sessionId) {
        if (!sessionId) throw new Error('Agent 未提供可以绑定的会话编号。');
        const original = room.sessionIds[agent];
        if (original && original !== sessionId) throw new Error('原生 Agent 尝试切换到其他会话。已保留原绑定。');
        if (original && !room.unconfirmedBindings.has(agent)) return;
        room.sessionIds[agent] = sessionId;
        const packet = { type: 'session', workspace: room.id, agent, run: active.id, source: active.source, sessionId };
        if (!room.confirmBinding) { this.emit(packet); return; }
        room.unconfirmedBindings.add(agent);
        const persisted = await new Promise(resolve => {
            const timer = setTimeout(() => this.bindingAck({ workspace: room.id, agent, run: active.id, sessionId }), 15000);
            this.bindingAcks.set(active.id, { ...packet, resolve, timer });
            this.emit({ ...packet, confirm: true });
        });
        if (!persisted) throw new Error('原生会话编号未能保存，请重试。');
        room.unconfirmedBindings.delete(agent);
    }

    bindingAck(packet) {
        const pending = this.bindingAcks.get(packet.run);
        if (!pending || pending.workspace !== packet.workspace || pending.agent !== packet.agent || pending.sessionId !== packet.sessionId) return;
        this.bindingAcks.delete(packet.run);
        clearTimeout(pending.timer);
        pending.resolve(packet.ok === true);
    }

    async run(room, agent, text, runId, origin, queued = false) {
        if (!room.agents.includes(agent)) throw new Error('请先打开对应的 Agent 聊天框。');
        if (room.turns.has(agent)) throw new Error(`${LABEL[agent]} 正在回复，请等本轮结束。`);
        if (room.configuring.has(agent)) throw new Error(`${LABEL[agent]} 正在切换设置，请稍等。`);
        if (typeof text !== 'string' || !text.trim() || text.length > 12000) throw new Error('消息必须在 1 到 12000 个字符之间。');
        const source = origin?.source;
        const data = { id: runId, replies: 0, cancelled: false, source, manual: origin?.manual };
        room.turns.set(agent, data);
        const notify = (type, payload) => this.emit({ ...payload, type, workspace: room.id, agent, run: runId, source });
        notify('start', { text, queued, remaining: room.queued.get(agent)?.length || 0 });
        let session;
        try {
            session = await this.openSession(room, agent);
            if (session.ensurePersistent) await session.ensurePersistent();
            await this.bindSession(room, agent, data, session.sessionId);
            if (data.cancelled) throw new Error('已停止。');
            const options = session.prepareSettings ? await session.prepareSettings(room.preferences[agent]) : {};
            if (data.cancelled) throw new Error('已停止。');
            notify('running', {});
            if (agent !== 'Hermes' && session.settingsFields)
                void session.settingsFields(room.preferences[agent]).then(fields => {
                    if (room.turns.get(agent) === data && !data.cancelled) notify('settingsPreview', { fields });
                }).catch(() => {});
            data.roster = agent === room.main ? this.roster(room) : null;
            const reply = await session.prompt(text, origin, data.roster, options);
            if (data.cancelled) throw new Error('已停止。');
            notify('finish', { ok: true, text: reply, progress: data.progress });
            return reply;
        } catch (error) {
            if (!session?.sessionId || session.peer?.closed || session.peer?.closing || /超时|进程已退出/i.test(error.message)) {
                session?.close();
                room.sessions.delete(agent);
            }
            const shown = data.cancelled ? new Error('已停止。') : error;
            notify('finish', { ok: false, text: safeError(shown), cancelled: data.cancelled });
            shown.handled = true;
            throw shown;
        } finally {
            this.rejectApprovals(room, agent);
            if (agent === room.main) this.cancelDelegations(room, agent);
            room.turns.delete(agent);
            void this.flush(room, agent);
        }
    }

    list(room, source) {
        return room.agents.filter(agent => agent !== source).map(agent => ({
            name: LABEL[agent], aliases: ALIASES[agent] || [], busy: room.turns.has(agent) || room.queued.has(agent) || room.configuring.has(agent)
        }));
    }

    async tool(roomId, source, name, args) {
        const room = this.rooms.get(roomId);
        const active = room?.turns.get(source);
        if (!active || room.main !== source || active.cancelled) throw new Error('没有已授权的主 Agent 回合。');
        if (name === 'list_agents') return JSON.stringify({ agents: this.list(room, source), roster: this.roster(room) });
        if (name !== 'ask_agent') throw new Error('未知协作工具。');
        const target = resolveAgent(args?.agent);
        if (!target) throw new Error('请选择已打开的 Agent。');
        return this.delegate(room, source, target, args?.message, false);
    }

    async delegate(room, source, target, text, manual, requestId = randomBytes(12).toString('hex')) {
        const active = room.turns.get(source);
        if (source !== room.main || target === source || !room.agents.includes(target)) throw new Error('请先打开目标 Agent。' + this.roster(room));
        if (room.turns.has(target) || room.queued.has(target) || room.configuring.has(target)) throw new Error(`${LABEL[target]} 正在处理请求。`);
        if (!manual && (!active || active.cancelled || active.replies >= 3)) throw new Error('当前主 Agent 回合已结束或已达到 3 次协作上限。');
        if (typeof text !== 'string' || !text.trim() || text.length > 10000) throw new Error('协作消息必须在 1 到 10000 个字符之间。');
        if (!manual) active.replies++;
        const link = (phase, body) => this.emit({ type: 'link', workspace: room.id, source, agent: target, run: requestId, phase, text: body });
        link('sending', text);
        try {
            const result = await this.run(room, target, text, requestId, { source, manual });
            link('returning', result);
            return result;
        } catch (error) {
            link(error.message === '已停止。' ? 'cancelled' : 'failed', safeError(error));
            throw error;
        }
    }

    stop(packet) {
        const room = this.rooms.get(packet.workspace);
        if (!room) return;
        const queued = room.queued.get(packet.agent);
        if (queued?.length) {
            room.queued.delete(packet.agent);
            this.emit({ type: 'queueCleared', workspace: room.id, agent: packet.agent, remaining: 0 });
        }
        this.cancelTurn(room, packet.agent);
        if (packet.agent === room.main) this.cancelDelegations(room, packet.agent);
    }

    cancelTurn(room, agent) {
        const active = room.turns.get(agent);
        if (!active || active.cancelled) return;
        active.cancelled = true;
        this.rejectApprovals(room, agent);
        room.sessions.get(agent)?.stop();
    }

    askApproval(room, agent, turn, payload) {
        const id = randomBytes(12).toString('hex');
        return new Promise(resolve => {
            const timer = setTimeout(() => this.approvalDecision(id, false), 120000);
            this.approvals.set(id, { room, agent, resolve, timer });
            this.emit({ type: 'approval', workspace: room.id, agent, run: turn.id, id, ...payload });
        });
    }

    approvalDecision(id, allowed) {
        const pending = this.approvals.get(id);
        if (!pending) return;
        this.approvals.delete(id);
        clearTimeout(pending.timer);
        pending.resolve(allowed === true);
    }

    rejectApprovals(room, agent) {
        for (const [id, pending] of this.approvals)
            if (pending.room === room && pending.agent === agent) this.approvalDecision(id, false);
    }

    cancelDelegations(room, source) {
        for (const [agent, data] of room.turns) {
            if (data.source === source && !data.manual) this.cancelTurn(room, agent);
        }
    }

    closeRoom(id) {
        const room = this.rooms.get(id);
        if (!room || room.turns.size || room.connecting.size || room.queued.size || room.configuring.size) return;
        for (const session of room.sessions.values()) session.close();
        this.rooms.delete(id);
    }

    close() {
        for (const pending of this.bindingAcks.values()) this.bindingAck(pending);
        for (const id of this.approvals.keys()) this.approvalDecision(id, false);
        for (const room of this.rooms.values()) {
            room.queued.clear();
            for (const session of room.sessions.values()) session.close();
        }
    }
}

const TOOL_LIST = [
    { name: 'list_agents', description: 'List open peer agents, their aliases, and whether they are busy. ' + ALIAS_GUIDE,
        inputSchema: { type: 'object', properties: {}, additionalProperties: false }, annotations: { readOnlyHint: true, openWorldHint: false } },
    { name: 'ask_agent', description: 'Send a focused request to an open peer and wait for its public reply. Use this for user-requested peer work once its prerequisites are ready. ' + ALIAS_GUIDE + ' ' + FILE_HANDOFF + ' This can spend the peer’s quota. Omit credentials and private reasoning. Report whether the peer replied or was unavailable.',
        inputSchema: { type: 'object', properties: { agent: { type: 'string', enum: AGENT_NAMES }, message: { type: 'string' } }, required: ['agent', 'message'], additionalProperties: false } }
];

function callBroker(endpoint, packet) {
    const { port, token } = JSON.parse(fs.readFileSync(endpoint, 'utf8'));
    const body = JSON.stringify(packet);
    return new Promise((resolve, reject) => {
        // A peer may be working for most of the native turn timeout before any response arrives.
        const request = http.request({
            hostname: '127.0.0.1', port, path: '/call', method: 'POST', timeout: TURN_TIMEOUT + OPEN_TIMEOUT,
            headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json', 'content-length': Buffer.byteLength(body) }
        }, response => {
            if (response.statusCode !== 200) {
                response.resume();
                reject(new Error(`本机协作连接返回 HTTP ${response.statusCode}。`));
                return;
            }
            let payload = '';
            response.setEncoding('utf8');
            response.on('data', chunk => { payload += chunk; });
            response.once('error', reject);
            response.once('end', () => {
                try { resolve(JSON.parse(payload)); } catch (error) { reject(error); }
            });
        });
        request.once('timeout', () => request.destroy(new Error('等待辅助 Agent 回复超时。')));
        request.once('error', reject);
        request.end(body);
    });
}

function runMcp(endpoint, room, source) {
    const output = message => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n');
    readline.createInterface({ input: process.stdin, crlfDelay: Infinity }).on('line', async line => {
        let msg;
        try { msg = parseLine(line); } catch { return; }
        if (msg?.id == null) return;
        try {
            let result;
            if (msg.method === 'initialize') {
                const known = ['2024-11-05', '2025-03-26', '2025-06-18', '2025-11-25'];
                result = { protocolVersion: known.includes(msg.params?.protocolVersion) ? msg.params.protocolVersion : known.at(-1), capabilities: { tools: {} }, serverInfo: { name: 'local-collaboration', version: '1.0.0' } };
            } else if (msg.method === 'ping') result = {};
            else if (msg.method === 'tools/list') result = { tools: TOOL_LIST };
            else if (msg.method === 'tools/call') {
                result = await callBroker(endpoint, { room, source, name: msg.params?.name, args: msg.params?.arguments });
            } else {
                output({ id: msg.id, error: { code: -32601, message: 'Unsupported MCP method' } });
                return;
            }
            output({ id: msg.id, result });
        } catch (error) { output({ id: msg.id, result: { isError: true, content: [{ type: 'text', text: safeError(error) }] } }); }
    });
}

async function readBody(request) {
    const chunks = [];
    let size = 0;
    for await (const chunk of request) {
        size += chunk.length;
        if (size > 64000) throw new Error('请求太大。');
        chunks.push(chunk);
    }
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function runHost(endpoint) {
    const emit = msg => process.stdout.write(JSON.stringify(msg) + '\n');
    const token = randomBytes(32).toString('hex');
    const host = new Coordinator(emit, endpoint);
    const server = http.createServer(async (request, response) => {
        const authorization = Buffer.from(request.headers.authorization || '');
        const expected = Buffer.from('Bearer ' + token);
        if (request.method !== 'POST' || request.url !== '/call' || authorization.length !== expected.length || !timingSafeEqual(authorization, expected)) {
            response.writeHead(403).end();
            return;
        }
        response.setHeader('Content-Type', 'application/json; charset=utf-8');
        try {
            const packet = await readBody(request);
            const text = await host.tool(packet.room, packet.source, packet.name, packet.args);
            response.end(JSON.stringify({ content: [{ type: 'text', text }] }));
        } catch (error) { response.end(JSON.stringify({ isError: true, content: [{ type: 'text', text: safeError(error) }] })); }
    });
    server.listen(0, '127.0.0.1', () => {
        fs.writeFileSync(endpoint, JSON.stringify({ port: server.address().port, token }), { mode: 0o600 });
        emit({ type: 'ready' });
        readline.createInterface({ input: process.stdin, crlfDelay: Infinity }).on('line', line => {
            let packet;
            try { packet = parseLine(line); } catch { return; }
            if (packet.type === 'send') host.fromUser(packet);
            else if (packet.type === 'connect') host.probe(packet);
            else if (packet.type === 'detectAgents') detectInstallations(emit, packet.id);
            else if (packet.type === 'agentSettings') host.agentSettings(packet);
            else if (packet.type === 'approvalDecision') host.approvalDecision(packet.id, packet.allow);
            else if (packet.type === 'sessionBound') host.bindingAck(packet);
            else if (packet.type === 'sync') {
                try { host.sync(packet.workspace); } catch {}
            }
            else if (packet.type === 'stop') host.stop(packet);
            else if (packet.type === 'closeWorkspace') host.closeRoom(packet.workspace);
        }).on('close', () => {
            host.close();
            server.close();
            try { fs.unlinkSync(endpoint); } catch {}
            setTimeout(() => process.exit(), 2200).unref();
        });
    });
}

if (require.main === module) {
    if (process.argv[2] === 'mcp') runMcp(process.argv[3], process.argv[4], process.argv[5]);
    else if (process.argv[2] === 'detect') process.stdout.write(JSON.stringify(scanInstallations()) + '\n');
    else runHost(process.argv[2]);
}

module.exports = { AgentSession, Coordinator, RpcPeer, recipe, windowsToWsl, runHost, scanInstallations };
