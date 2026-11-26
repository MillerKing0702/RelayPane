'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const readline = require('node:readline');
const { once } = require('node:events');
const { spawn } = require('node:child_process');
const { AgentSession, Coordinator, recipe, windowsToWsl } = require('../src/AgentBridge.cjs');
const workspaceAliases = /GPT → Codex, DeepSeek \/ DeepSeek Harness → DSH, Gemini → Antigravity/;

function wire(protocol, agent, main) {
    let pending;
    let permission;
    const output = value => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...value }) + '\n');
    const sendReply = msg => {
        const session = msg.params.sessionId;
        output({ method: 'session/update', params: { sessionId: session, update: { sessionUpdate: 'agent_thought_chunk', content: { type: 'text', text: 'PRIVATE' } } } });
        output({ method: 'session/update', params: { sessionId: 'other-session', update: { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'OTHER' } } } });
        if (msg.params.prompt.at(-1).text === 'split') {
            output({ method: 'session/update', params: { sessionId: session, update: { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'Progress only' } } } });
            output({ method: 'session/update', params: { sessionId: session, update: { sessionUpdate: 'tool_call', toolCallId: 'read' } } });
        }
        output({ method: 'session/update', params: { sessionId: session, update: { sessionUpdate: 'agent_message_chunk', messageId: 'answer',
            content: { type: 'text', text: 'OK' } } } });
        if (msg.params.prompt.at(-1).text === 'split') {
            output({ method: 'session/update', params: { sessionId: session, update: { sessionUpdate: 'agent_message_chunk', messageId: 'footer',
                content: { type: 'text', text: 'Final detail' } } } });
        }
        output({ id: msg.id, result: { stopReason: 'end_turn' } });
    };
    readline.createInterface({ input: process.stdin }).on('line', line => {
        const msg = JSON.parse(line);
        const respond = result => output({ id: msg.id, result });
        if (msg.method === 'initialize') respond({ protocolVersion: 1, agentCapabilities: { sessionCapabilities: { resume: {} } } });
        if (msg.method === 'account/read') {
            assert.equal(msg.params.refreshToken, false);
            respond({ account: { type: 'chatgpt' }, requiresOpenaiAuth: true });
        }
        if (msg.method === 'session/new') respond({ sessionId: 'opened' });
        if (msg.method === 'session/resume') respond({});
        if (msg.method === 'thread/start' || msg.method === 'thread/resume') {
            assert.equal(msg.params.sandbox, undefined);
            assert.equal(msg.params.approvalPolicy, 'never');
            assert.match(msg.params.developerInstructions, /send the absolute path/);
            assert.match(msg.params.developerInstructions, agent === main ? /you are Codex, the main agent/i : new RegExp(`Codex, an assisting agent.*${main} is the main agent`));
            assert.match(msg.params.developerInstructions, workspaceAliases);
            respond({ thread: { id: 'thread' } });
        }
        if (msg.method === 'thread/inject_items') {
            assert.equal(msg.params.threadId, 'thread');
            assert.equal(msg.params.items[0].role, 'developer');
            respond({});
        }
        if (msg.method === 'session/prompt') {
            const [context, original] = msg.params.prompt;
            assert.match(context.text, agent === main ? /the main agent in this Collaboration Chat workspace/ : new RegExp(`an assisting agent.*${main} is the main agent`));
            assert.match(context.text, workspaceAliases);
            assert.match(context.text, /send the absolute path/);
            const input = original.text;
            if (input === '  **delegated**\r\n\n') assert.match(context.text, new RegExp(`delegated by the main agent, ${main}`));
            else if (input === 'manual') assert.match(context.text, /user sent the following request directly/);
            if (input === 'cancel') { pending = msg; return; }
            if (input === 'ask') {
                permission = msg;
                output({ id: 'permission', method: 'session/request_permission', params: { sessionId: msg.params.sessionId,
                    toolCall: { title: 'Mock tool' }, options: [{ kind: 'allow_once', optionId: 'once' }, { kind: 'reject_once', optionId: 'no' }] } });
            } else sendReply(msg);
        }
        if (msg.id === 'permission' && !msg.method) {
            assert.equal(msg.result.outcome.optionId, 'once');
            sendReply(permission);
        }
        if (msg.method === 'session/cancel' && pending) output({ id: pending.id, result: { stopReason: 'cancelled' } });
        if (msg.method === 'turn/start' && protocol === 'codex') {
            const input = msg.params.input.at(-1).text;
            respond({ turn: { id: 'turn' } });
            if (input === 'cancel') return;
            output({ method: 'item/reasoning/textDelta', params: { threadId: 'thread', delta: 'PRIVATE' } });
            if (input === '  **delegated**\r\n\n') {
                assert.match(msg.params.input[0].text, new RegExp(`delegated by the main agent, ${main}`));
            }
            if (input === 'split' || input === '  **delegated**\r\n\n') {
                output({ method: 'item/completed', params: { threadId: 'thread', turnId: 'turn', item: { type: 'agentMessage', id: 'progress', phase: 'commentary', text: 'Progress only' } } });
                output({ method: 'item/completed', params: { threadId: 'thread', turnId: 'turn', item: { type: 'agentMessage', id: 'marker', phase: 'commentary', text: '::browser-complete{title="Stopped browser session verification"}' } } });
            }
            output({ method: 'item/agentMessage/delta', params: { threadId: 'thread', turnId: 'turn', itemId: 'message', delta: 'O' } });
            output({ method: 'item/completed', params: { threadId: 'thread', turnId: 'turn', item: { type: 'agentMessage', id: 'message', phase: 'final_answer', text: 'OK' } } });
            output({ method: 'turn/completed', params: { threadId: 'thread', turn: { id: 'turn', status: 'completed' } } });
        }
        if (msg.method === 'turn/interrupt') {
            assert.equal(msg.params.threadId, 'thread');
            respond({});
            output({ method: 'turn/completed', params: { threadId: 'thread', turn: { id: 'turn', status: 'interrupted' } } });
        }
    });
}

if (process.argv[2] === 'wire') {
    wire(...process.argv.slice(3));
} else {
    const test = require('node:test');
    const room = { id: '12000000000000000000000000000000', main: 'Codex', directory: os.tmpdir(), write: false,
        agents: ['Codex', 'Dsh', 'Antigravity', 'Hermes'], sessionIds: {} };

    for (const agent of [...room.agents, 'ClaudeCode', 'GeminiCli', 'OpenCode', 'QwenCode']) {
        test(`${agent}: native message and resume protocol, private events excluded`, async () => {
            const protocol = agent === 'Codex' ? 'codex' : 'acp';
            const events = [];
            const session = new AgentSession(agent, room, (type, value) => { events.push({ type, value }); return false; }, null,
                () => ({ protocol, command: process.execPath, args: [__filename, 'wire', protocol, agent, room.main], cwd: os.tmpdir() }));
            try {
                await session.connect(protocol === 'codex' ? 'thread' : 'saved-id');
                assert.equal(session.sessionId, protocol === 'codex' ? 'thread' : 'saved-id');
                assert.match(await session.checkConnection(), /已连接/);
                assert.equal(await session.prompt(protocol === 'acp' ? 'ask' : 'prompt'), 'OK');
                assert.equal(events.filter(event => event.type === 'accepted').length, 1);
                assert.equal(JSON.stringify(events).includes('PRIVATE'), false);
                assert.equal(JSON.stringify(events).includes('OTHER'), false);
                assert.equal(await session.prompt('split'), protocol === 'acp' ? 'OK\n\nFinal detail' : 'OK');
                assert.equal(events.filter(event => event.type === 'presentation').at(-1).value.progress, 'Progress only');
                assert.equal(events.some(event => event.type === 'approval'), false);
                const id = session.sessionId;
                const pending = session.prompt('cancel');
                session.stop();
                await assert.rejects(pending, /已停止|未完成/);
                assert.equal(await session.prompt('follow-up'), 'OK');
                assert.equal(session.sessionId, id);
            } finally { session.close(); }
        });
    }

    test('Codex assistant and ACP mains receive their role on the native protocol, including restored sessions', async () => {
        for (const agent of room.agents) {
            const main = agent === 'Codex' ? 'Antigravity' : agent;
            const protocol = agent === 'Codex' ? 'codex' : 'acp';
            const session = new AgentSession(agent, { ...room, main }, () => {}, null,
                () => ({ protocol, command: process.execPath, args: [__filename, 'wire', protocol, agent, main], cwd: os.tmpdir() }));
            try {
                await session.connect(agent === 'Codex' ? undefined : 'saved-id');
                await session.ensurePersistent();
                assert.equal(await session.prompt(agent === 'Codex' ? '  **delegated**\r\n\n' : 'main request',
                    agent === 'Codex' ? { source: main, manual: false } : undefined), 'OK');
            } finally { session.close(); }
        }
    });

    test('coordinator routes only to open peers, enforces budget and reports success/failure', async () => {
        const packets = [];
        let coordinator;
        coordinator = new Coordinator(value => packets.push(value), 'endpoint', (agent, workspace, observer, mcp) => ({
            async connect() { this.sessionId = agent; observer('session', { sessionId: agent }); },
            async prompt(text, origin) {
                if (agent === 'Codex') {
                    assert.ok(mcp);
                    const { agents: peers } = JSON.parse(await coordinator.tool(room.id, agent, 'list_agents'));
                    assert.deepEqual(peers.map(peer => [peer.name, peer.aliases]), [['DSH', ['DeepSeek', 'DeepSeek Harness']], ['Antigravity', ['Gemini']], ['Hermes', []]]);
                    assert.equal(await coordinator.tool(room.id, agent, 'ask_agent', { agent: 'DeepSeek', message: 'one' }), 'DSH reply');
                    await assert.rejects(coordinator.tool(room.id, agent, 'ask_agent', { agent: 'Hermes', message: 'fail' }), /mock failure/);
                    assert.equal(await coordinator.tool(room.id, agent, 'ask_agent', { agent: 'gEmInI', message: 'three' }), 'Antigravity reply');
                    await assert.rejects(coordinator.tool(room.id, agent, 'ask_agent', { agent: 'DSH', message: 'four' }), /3 次/);
                    return 'main reply';
                }
                assert.equal(mcp, null);
                assert.deepEqual(origin, { source: 'Codex', manual: false });
                if (text === 'fail') throw new Error('mock failure');
                return (agent === 'Dsh' ? 'DSH' : agent) + ' reply';
            },
            close() {}
        }));
        await assert.rejects(coordinator.tool(room.id, 'Codex', 'list_agents', {}), /已授权/);
        await coordinator.fromUser({ workspace: room, id: 'root', agent: 'Codex', text: 'start' });
        const phases = agent => packets.filter(item => item.type === 'link' && item.agent === agent).map(item => item.phase);
        assert.deepEqual(phases('Dsh'), ['sending', 'returning']);
        assert.deepEqual(phases('Hermes'), ['sending', 'failed']);
        assert.deepEqual(phases('Antigravity'), ['sending', 'returning']);
        assert.equal(packets.some(item => item.agent === 'Codex' && item.type === 'finish' && item.ok), true);
        const scoped = { ...room, agents: ['Codex'] };
        await coordinator.fromUser({ workspace: scoped, id: 'manual', agent: 'Codex', target: 'Dsh', text: 'try' });
        assert.match(packets.at(-1).text, /打开目标 Agent/);
    });

    test('localhost discovery rejects unauthenticated requests and transports MCP tools', async () => {
        const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'collaboration-broker-test-'));
        const endpoint = path.join(dir, 'endpoint.json');
        const host = spawn(process.execPath, [path.join(__dirname, '..', 'src', 'AgentBridge.cjs'), endpoint], { windowsHide: true });
        try {
            const input = readline.createInterface({ input: host.stdout });
            const [ready] = await once(input, 'line');
            assert.equal(JSON.parse(ready).type, 'ready');
            const config = JSON.parse(fs.readFileSync(endpoint, 'utf8'));
            const url = `http://127.0.0.1:${config.port}/call`;
            const unauthenticated = await fetch(url, { method: 'POST' });
            assert.equal(unauthenticated.status, 403);
            const mcp = spawn(process.execPath, [path.join(__dirname, '..', 'src', 'AgentBridge.cjs'), 'mcp', endpoint, room.id, 'Codex'], { windowsHide: true });
            try {
                const lines = readline.createInterface({ input: mcp.stdout });
                const next = once(lines, 'line');
                mcp.stdin.write('\ufeff' + JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/list' }) + '\n');
                assert.equal(JSON.parse((await next)[0]).result.tools.length, 2);
                const call = once(lines, 'line');
                mcp.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'list_agents', arguments: {} } }) + '\n');
                assert.equal(JSON.parse((await call)[0]).result.isError, true);
            } finally { mcp.stdin.end(); await once(mcp, 'exit'); }
        } finally { host.stdin.end(); await once(host, 'exit'); }
        assert.equal(fs.existsSync(endpoint), false);
    });

    test('each of the four providers can be main and route to each of the other three', async () => {
        const names = { Codex: 'GPT', Dsh: 'DeepSeek Harness', Antigravity: 'Gemini', Hermes: 'Hermes' };
        for (const main of room.agents) {
            const activeRoom = { ...room, main };
            const routes = [];
            let coordinator;
            coordinator = new Coordinator(event => { if (event.type === 'link' && event.phase === 'returning') routes.push([event.source, event.agent]); },
                'temporary', (agent, workspace, observer, mcp) => ({
                    async connect() { this.sessionId = agent; },
                    async prompt() {
                        if (agent === main) {
                            assert.ok(mcp);
                            for (const target of activeRoom.agents.filter(peer => peer !== main))
                                await coordinator.tool(activeRoom.id, main, 'ask_agent', { agent: names[target], message: 'a short query' });
                        } else assert.equal(mcp, null);
                        return 'OK';
                    },
                    close() {}
                }));
            await coordinator.fromUser({ workspace: activeRoom, id: 'matrix', agent: main, text: 'delegate' });
            assert.deepEqual(routes, activeRoom.agents.filter(agent => agent !== main).map(target => [main, target]));
        }
    });

    test('one-click connection reports each pane without saving unstarted sessions', async () => {
        const events = [];
        const constructions = [];
        let modelCalls = 0;
        const coordinator = new Coordinator(event => events.push(event), 'temporary', (agent) => {
            constructions.push(agent);
            return {
                async connect() { this.sessionId = agent + '-session'; },
                async checkConnection() {
                    if (agent === 'Hermes') throw new Error('mock unavailable');
                    return 'ready';
                },
                async prompt() { modelCalls++; return 'OK'; },
                close() {}
            };
        });

        await coordinator.probe({ workspace: room, id: 'check' });
        assert.equal(modelCalls, 0);
        assert.equal(events.length, 4);
        assert.equal(events.every(event => event.type === 'connectionResult' && event.run === 'check'), true);
        assert.equal(events.find(event => event.agent === 'Hermes').ok, false);
        assert.deepEqual(coordinator.rooms.get(room.id).sessionIds, {});

        await coordinator.fromUser({ workspace: room, id: 'message', agent: 'Dsh', text: 'reply' });
        assert.equal(modelCalls, 1);
        assert.equal(constructions.filter(agent => agent === 'Dsh').length, 1);
        assert.equal(coordinator.rooms.get(room.id).sessionIds.Dsh, 'Dsh-session');
    });

    test('setup changes reach the first main prompt as a roster and do not start a turn', async () => {
        const inputs = [];
        const coordinator = new Coordinator(() => {}, 'fixture', () => ({
            async connect() { this.sessionId = 'hermes'; },
            async prompt(text, origin, roster) { inputs.push({ text, roster }); return 'ready'; },
            close() {}
        }));
        const firstSetup = { ...room, main: 'Hermes', agents: ['Hermes', 'Codex', 'Antigravity'] };
        const selected = { ...firstSetup, agents: ['Hermes', 'Dsh', 'Antigravity'] };
        coordinator.sync(firstSetup);
        coordinator.sync(selected);
        assert.deepEqual(inputs, []);
        await coordinator.fromUser({ workspace: selected, id: 'first', agent: 'Hermes', text: '  初次提问\n\n' });
        assert.deepEqual(inputs, [{ text: '  初次提问\n\n',
            roster: '用户选择了 Hermes 作为主 Agent。当前辅助 Agent：DSH（DeepSeek）、Antigravity（Gemini）。' }]);
    });

    test('a failed resume retains the original native ID and never opens a replacement', async () => {
        const events = [];
        const connections = [];
        const workspace = { ...room, sessionIds: { Dsh: 'retired' } };
        const coordinator = new Coordinator(event => events.push(event), 'fixture', () => ({
            async connect(oldId) {
                connections.push(oldId);
                if (oldId) throw new Error('old native session missing');
                this.sessionId = 'replacement';
            },
            async prompt() { return 'new reply'; },
            close() {}
        }));
        const send = id => coordinator.fromUser({ workspace, id, agent: 'Dsh', text: 'hello' });
        await send('old');
        assert.equal(events.find(event => event.run === 'old' && event.type === 'finish').ok, false);
        await send('retry');
        assert.deepEqual(connections, ['retired', 'retired']);
        assert.equal(coordinator.rooms.get(room.id).sessionIds.Dsh, 'retired');
        assert.throws(() => coordinator.room({ ...workspace, main: 'Dsh' }), /已绑定/);
    });

    test('a first turn waits for the desktop binding, even when stopped before a reply', async () => {
        const events = [];
        const inputs = [];
        let storeAllowed = false;
        let coordinator;
        const workspace = { ...room, confirmBinding: true };
        coordinator = new Coordinator(event => {
            events.push(event);
            if (event.type === 'session') {
                assert.equal(event.confirm, true);
                if (event.run === 'stopped') coordinator.stop({ workspace: room.id, agent: 'Codex' });
                coordinator.bindingAck({ ...event, ok: storeAllowed });
            }
        }, 'fixture', () => ({
            async connect(old) { this.sessionId = old || 'bound-codex'; },
            async ensurePersistent() { assert.equal(inputs.length, 0); },
            async prompt(text) { inputs.push(text); return 'OK'; },
            stop() {},
            close() {}
        }));
        const send = id => coordinator.fromUser({ workspace, id, agent: 'Codex', text: id });
        await send('disk-failed');
        assert.match(events.at(-1).text, /未能保存/);
        storeAllowed = true;
        await send('stopped');
        assert.equal(events.at(-1).cancelled, true);
        await send('next');
        assert.deepEqual(inputs, ['next']);
        assert.equal(coordinator.rooms.get(room.id).sessionIds.Codex, 'bound-codex');
    });

    test('missing historic bindings are blocked instead of auto-starting a different conversation', async () => {
        const events = [];
        const coordinator = new Coordinator(event => events.push(event), 'fixture', () => {
            assert.fail('must not create a replacement session');
        });
        await coordinator.fromUser({ workspace: { ...room, missingBindings: ['Dsh'] }, id: 'legacy', agent: 'Dsh', text: 'hello' });
        assert.match(events.at(-1).text, /缺少.*原会话编号/);
    });

    test('an interrupted turn keeps its native session ID and leaves direct peer messages running', async () => {
        const events = [];
        const connected = [];
        const active = new Map();
        const coordinator = new Coordinator(event => events.push(event), 'temporary', (agent, workspace, observer) => ({
            async connect(oldId) {
                connected.push([agent, oldId]);
                this.sessionId = oldId || agent + '-session';
            },
            async prompt(text) {
                observer('accepted', { sessionId: this.sessionId });
                if (text === 'resume') return 'resumed';
                return new Promise(resolve => active.set(agent, resolve));
            },
            stop() { active.get(agent)(); },
            close() {}
        }));
        const main = coordinator.fromUser({ workspace: room, id: 'main', agent: 'Codex', text: 'start' });
        const independent = coordinator.fromUser({ workspace: room, id: 'direct', agent: 'Codex', target: 'Dsh', text: 'manual' });
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(events.find(item => item.run === 'main' && item.type === 'session').sessionId, 'Codex-session');
        coordinator.stop({ workspace: room.id, agent: 'Codex' });
        await main;
        assert.equal(events.find(item => item.type === 'finish' && item.run === 'main').cancelled, true);
        active.get('Dsh')('independent reply');
        await independent;
        assert.equal(events.find(item => item.type === 'finish' && item.run === 'direct').ok, true);
        await coordinator.fromUser({ workspace: room, id: 'next', agent: 'Codex', text: 'resume' });
        assert.deepEqual(connected.filter(([agent]) => agent === 'Codex'), [['Codex', undefined], ['Codex', 'Codex-session']]);
    });

    test('an ACP main receives queued messages once, in order, with the latest roster', async () => {
        const events = [];
        const inputs = [];
        let complete;
        const workspace = { ...room, main: 'Dsh' };
        const coordinator = new Coordinator(event => events.push(event), 'fixture', () => ({
            async connect() { this.sessionId = 'dsh'; },
            async prompt(text, origin, roster) {
                inputs.push([text, roster]);
                if (text === 'start') return new Promise(resolve => { complete = resolve; });
                return 'answered';
            },
            close() {}
        }));
        const send = (id, text, settings = workspace) => coordinator.fromUser({ workspace: settings, id, agent: 'Dsh', text });
        const first = send('first', 'start');
        await new Promise(resolve => setImmediate(resolve));
        await send('q1', 'after 1');
        const rosterNotice = '08:12:04 用户已移除 Antigravity（Gemini）。';
        const updated = { ...workspace, agents: ['Dsh', 'Codex', 'Hermes'], rosterNotice };
        const updatedRoster = '用户选择了 DSH（DeepSeek） 作为主 Agent。当前辅助 Agent：Codex（GPT）、Hermes。\n\n' + rosterNotice;
        coordinator.sync(updated);
        await send('q2', 'after 2', updated);
        const roster = JSON.parse(await coordinator.tool(room.id, 'Dsh', 'list_agents'));
        assert.equal(roster.roster, updatedRoster);
        assert.equal(roster.agents.some(agent => agent.name === 'Antigravity'), false);
        await assert.rejects(coordinator.tool(room.id, 'Dsh', 'ask_agent', { agent: 'Gemini', message: 'old' }), /用户已移除/);
        complete('first done');
        await first;
        await new Promise(resolve => setImmediate(resolve));
        assert.deepEqual(inputs.slice(1), [['after 1', updatedRoster], ['after 2', updatedRoster]]);
        assert.deepEqual(events.filter(event => event.type === 'start' && event.queued).map(event => event.run), ['q1', 'q2']);
    });

    test('Codex retries a rejected roster notice before steering user input once', { timeout: 3000 }, async () => {
        const events = [];
        const steered = [];
        let complete;
        let acknowledge;
        const delivered = new Promise(resolve => { acknowledge = resolve; });
        const coordinator = new Coordinator(event => {
            events.push(event);
            if (event.type === 'steered') acknowledge();
        }, 'fixture', (agent, workspace, observer) => ({
            async connect() { this.sessionId = 'codex'; },
            async prompt() {
                observer('accepted', { sessionId: this.sessionId });
                return new Promise(resolve => { complete = resolve; });
            },
            async steer(text) {
                steered.push(text);
                return steered.length > 1;
            },
            close() {}
        }));
        const first = coordinator.fromUser({ workspace: room, id: 'first', agent: 'Codex', text: 'start' });
        await new Promise(resolve => setImmediate(resolve));
        const rosterNotice = '08:12:04 用户已移除 Hermes。';
        const updated = { ...room, agents: ['Codex', 'Dsh', 'Antigravity'], rosterNotice };
        const updatedRoster = '用户选择了 Codex（GPT） 作为主 Agent。当前辅助 Agent：DSH（DeepSeek）、Antigravity（Gemini）。\n\n' + rosterNotice;
        coordinator.sync(updated);
        await coordinator.fromUser({ workspace: updated, id: 'q1', agent: 'Codex', text: 'correction' });
        await delivered;
        complete('done');
        await first;
        assert.deepEqual(steered, [updatedRoster, updatedRoster, 'correction']);
        assert.equal(events.filter(event => event.type === 'steered').length, 1);
        assert.deepEqual(events.filter(event => event.type === 'start').map(event => event.run), ['first']);
    });

    test('a send at the end of a Codex turn cannot jump ahead of a pending follow-up', async () => {
        const inputs = [];
        let finish;
        let rejectSteer;
        const coordinator = new Coordinator(() => {}, 'fixture', (agent, workspace, observer) => ({
            async connect() { this.sessionId = 'codex'; },
            async prompt(text) {
                inputs.push(text);
                observer('accepted', { sessionId: this.sessionId });
                if (text === 'start') return new Promise(resolve => { finish = resolve; });
                return 'done';
            },
            async steer() {
                if (rejectSteer) return false;
                return new Promise(resolve => { rejectSteer = () => resolve(false); });
            },
            close() {}
        }));
        const send = text => coordinator.fromUser({ workspace: room, id: text, agent: 'Codex', text });
        const initial = send('start');
        await new Promise(resolve => setImmediate(resolve));
        await send('queued first');
        finish('done');
        await initial;
        await send('queued second');
        rejectSteer();
        await new Promise(resolve => setImmediate(resolve));
        assert.deepEqual(inputs, ['start', 'queued first', 'queued second']);
    });

    test('when the main turn fails its outstanding delegate is cancelled', async () => {
        const events = [];
        let review;
        let finishReview;
        const coordinator = new Coordinator(event => events.push(event), 'temporary', agent => ({
            async connect() { this.sessionId = agent; },
            async prompt() {
                if (agent === 'Codex') {
                    review = coordinator.tool(room.id, agent, 'ask_agent', { agent: 'Gemini', message: 'check' }).catch(error => error.message);
                    await new Promise(resolve => setImmediate(resolve));
                    throw new Error('main disconnected');
                }
                return new Promise(resolve => { finishReview = resolve; });
            },
            stop() { finishReview('cancelled result'); },
            close() {}
        }));
        await coordinator.fromUser({ workspace: room, id: 'main-fails', agent: 'Codex', text: 'start' });
        assert.equal(await review, '已停止。');
        assert.deepEqual(events.filter(item => item.type === 'link').map(item => item.phase), ['sending', 'cancelled']);
    });

    test('ACP permissions are answered without prompting and cannot approve stopped or unrelated sessions', async () => {
        const workspace = { ...room, main: 'Antigravity', agents: ['Antigravity', 'Dsh'] };
        const main = new AgentSession('Antigravity', workspace, () => { assert.fail('unnecessary interactive approval'); },
            { name: 'local_collaboration' }, () => ({ protocol: 'acp' }));
        main.sessionId = 'local-session';
        main.turn = { cancelled: false };
        const options = [{ kind: 'allow_once', optionId: 'once' }, { kind: 'reject_once', optionId: 'deny' }];
        const list = { name: 'mcp_local_collaboration_list_agents', title: 'Run mcp_local_collaboration_list_agents', kind: 'other' };
        const ask = { name: 'mcp_local_collaboration_ask_agent', title: 'Run mcp_local_collaboration_ask_agent', kind: 'other',
            rawInput: { agent: 'DSH', message: 'test' } };
        const decide = (toolCall, sessionId = main.sessionId, offered = options) => main.serverRequest('session/request_permission', { sessionId, toolCall, options: offered });

        assert.equal((await decide(list)).outcome.optionId, 'once');
        assert.equal((await decide(ask)).outcome.optionId, 'once');
        assert.equal((await decide({ ...ask, rawInput: { agent: 'DeepSeek Harness', message: 'test' } })).outcome.optionId, 'once');
        assert.equal((await decide({ ...list, kind: 'execute' })).outcome.optionId, 'once');
        assert.equal((await decide({ ...list, name: 'run_command' })).outcome.optionId, 'once');
        assert.equal((await decide({ ...list, locations: [{ path: '/file' }] })).outcome.optionId, 'once');
        assert.equal((await decide(list, 'different-session')).outcome.optionId, 'deny');
        main.turn.cancelled = true;
        assert.equal((await decide(ask)).outcome.optionId, 'deny');
    });

    test('delegation transfers the entire Markdown message and reply without rewriting whitespace', async () => {
        const source = '    indented code\r\n\r\n## 标题\n\n| 列 | 值 |\n| --- | --- |\n| **甲** | `乙` |\n\n  ';
        const reply = '## 原样返回\n\n' + source;
        const events = [];
        let seen;
        const coordinator = new Coordinator(packet => events.push(packet), 'fixture', () => ({
            async connect() { this.sessionId = 'peer'; },
            async prompt(value, origin) { seen = value; assert.deepEqual(origin, { source: 'Codex', manual: true }); return reply; },
            close() {}
        }));
        await coordinator.fromUser({ workspace: room, agent: 'Codex', target: 'Dsh', id: 'formatting', text: source });
        assert.equal(seen, source);
        assert.equal(events.find(event => event.phase === 'sending').text, source);
        assert.equal(events.find(event => event.phase === 'returning').text, reply);
    });

    test('WSL cwd handles local drives and only the selected distribution', () => {
        assert.equal(windowsToWsl('D:\\work\\with space', 'Ubuntu'), '/mnt/d/work/with space');
        assert.equal(windowsToWsl('\\\\wsl.localhost\\Ubuntu\\home\\demo', 'Ubuntu'), '/home/demo');
        assert.throws(() => windowsToWsl('\\\\wsl.localhost\\Other\\home', 'Ubuntu'), /WSL/);
    });

    test('ACP launch recipes retain provider-owned execution preferences', () => {
        const before = { agy: process.env.COLLAB_AGY, dsh: process.env.COLLAB_DSH, mode: process.env.DSH_PERMISSION_MODE };
        try {
            process.env.COLLAB_AGY = process.execPath;
            process.env.COLLAB_DSH = __filename;
            process.env.DSH_PERMISSION_MODE = 'native-policy-fixture';
            const dir = { directory: os.tmpdir() };
            const dsh = recipe('Dsh', dir);
            assert.equal(dsh.env.DSH_PERMISSION_MODE, 'native-policy-fixture');
            delete process.env.DSH_PERMISSION_MODE;
            assert.equal(recipe('Dsh', dir).env.DSH_PERMISSION_MODE, undefined);
            assert.deepEqual(recipe('Antigravity', dir).args, ['acp']);
        } finally {
            for (const [key, value] of [['COLLAB_AGY', before.agy], ['COLLAB_DSH', before.dsh], ['DSH_PERMISSION_MODE', before.mode]]) {
                if (value === undefined) delete process.env[key];
                else process.env[key] = value;
            }
        }
    });

    test('additional ACP agents start installed entry points with no shell or package download', () => {
        const base = fs.mkdtempSync(path.join(os.tmpdir(), 'relaypane-acp-packages-'));
        const previous = process.env.APPDATA;
        const deps = [
            ['ClaudeCode', 'claude-agent-acp', '@agentclientprotocol/claude-agent-acp', []],
            ['GeminiCli', 'gemini', '@google/gemini-cli', ['--acp']],
            ['OpenCode', 'opencode', 'opencode-ai', ['acp']],
            ['QwenCode', 'qwen', '@qwen-code/qwen-code', ['--acp']]
        ];
        try {
            process.env.APPDATA = base;
            for (const [agent, bin, name, args] of deps) {
                const directory = path.join(base, 'npm', 'node_modules', name);
                fs.mkdirSync(directory, { recursive: true });
                const entry = path.join(directory, 'main.js');
                fs.writeFileSync(entry, '/* test entry */');
                fs.writeFileSync(path.join(directory, 'package.json'), JSON.stringify({ name, bin: { [bin]: 'main.js' } }));
                const spec = recipe(agent, { directory: base });
                assert.equal(spec.protocol, 'acp');
                // A real executable on PATH may take priority over the test npm entry.
                if (spec.command === process.execPath) assert.deepEqual(spec.args, [entry, ...args]);
                else assert.deepEqual(spec.args, args);
            }
        } finally {
            if (previous === undefined) delete process.env.APPDATA;
            else process.env.APPDATA = previous;
            if (path.dirname(path.resolve(base)) === path.resolve(os.tmpdir()))
                fs.rmSync(base, { recursive: true, force: true });
        }
    });

    test('additional Codex permissions are scoped to the matching active turn', async () => {
        const session = new AgentSession('Codex', room, () => { assert.fail('unnecessary interactive approval'); }, null, () => ({ protocol: 'codex' }));
        session.sessionId = 'original';
        session.turn = {};
        const permissions = { network: { enabled: true } };
        assert.deepEqual(await session.serverRequest('item/permissions/requestApproval', { threadId: 'original', permissions }), { permissions, scope: 'turn' });
        assert.deepEqual(await session.serverRequest('item/permissions/requestApproval', { threadId: 'different', permissions }), { permissions: {}, scope: 'turn' });
        session.turn.cancelled = true;
        assert.deepEqual(await session.serverRequest('item/permissions/requestApproval', { threadId: 'original', permissions }), { permissions: {}, scope: 'turn' });
    });

    test('ACP applies model, refreshed effort, and mode through the native session options', async () => {
        const session = new AgentSession('Antigravity', { ...room, main: 'Antigravity' }, () => {}, null, () => ({ protocol: 'acp' }));
        session.sessionId = 'native-session';
        let selected = { model: 'fast', thought_level: 'low', mode: 'default' };
        const field = (id, category, values) => ({ id, category, type: 'select', currentValue: selected[id],
            options: values.map(value => ({ value, name: value })) });
        const configOptions = () => [
            field('model', 'model', ['fast', 'pro']),
            field('thought_level', 'thought_level', selected.model === 'pro' ? ['low', 'high'] : ['low']),
            field('mode', 'mode', ['default', 'plan'])
        ];
        const writes = [];
        session.rememberAcpSettings({ configOptions: configOptions() });
        session.peer = { async call(method, params) {
            assert.equal(method, 'session/set_config_option');
            assert.equal(params.sessionId, 'native-session');
            writes.push([params.configId, params.value]);
            selected[params.configId] = params.value;
            return { configOptions: configOptions() };
        } };
        const fields = await session.applySettings({ model: 'pro', effort: 'high', mode: 'plan' });
        assert.deepEqual(writes, [['model', 'pro'], ['thought_level', 'high'], ['mode', 'plan']]);
        assert.deepEqual(fields.map(item => [item.id, item.current]), [['model', 'pro'], ['effort', 'high'], ['mode', 'plan']]);
        await assert.rejects(session.applySettings({ effort: 'ultra' }), /不可用/);
    });

    test('saved settings apply independently for each agent and a model change clears the old effort', async () => {
        const receipts = [];
        const applied = [];
        const workspace = { ...room, preferences: { Codex: { model: 'old', effort: 'high' }, Dsh: { model: 'dsh-model' } } };
        const coordinator = new Coordinator(event => receipts.push(event), 'fixture', agent => ({
            async connect() { this.sessionId = agent; },
            async applySettings() { return []; },
            async prepareSettings(preferences) { applied.push([agent, preferences]); return { model: preferences.model }; },
            async prompt(text, origin, roster, options) { return options.model; },
            close() {}
        }));
        await coordinator.agentSettings({ workspace, agent: 'Codex', id: 'settings', change: { id: 'model', value: 'new' } });
        assert.deepEqual(receipts[0].preferences, { model: 'new' });
        for (const agent of ['Codex', 'Dsh']) await coordinator.fromUser({ workspace, agent, id: agent, text: 'test' });
        assert.deepEqual(applied, [['Codex', { model: 'new' }], ['Dsh', { model: 'dsh-model' }]]);
    });

    test('Codex combines the selected model, effort and permissions with plan mode, and asks only when opted in', async () => {
        const approvals = [];
        const session = new AgentSession('Codex', room, (type, payload) => { approvals.push(payload.text); return false; },
            null, () => ({ protocol: 'codex' }));
        session.sessionId = 'native-thread';
        session.codexDefaults = { model: 'default-model', reasoningEffort: 'low' };
        session.catalog = {
            models: [{ model: 'selected', defaultReasoningEffort: 'medium', supportedReasoningEfforts: [{ reasoningEffort: 'high' }] }],
            modes: [{ mode: 'plan', name: 'Plan' }], access: [{ id: ':read-only', allowed: true }]
        };
        const options = await session.prepareSettings({ model: 'selected', effort: 'high', mode: 'plan', access: ':read-only', approval: 'on-request' });
        assert.deepEqual(options, { model: 'selected', effort: 'high', permissions: ':read-only', approvalPolicy: 'on-request',
            collaborationMode: { mode: 'plan', settings: { model: 'selected', reasoning_effort: 'high', developer_instructions: null } } });
        assert.deepEqual(await session.prepareSettings({}), { approvalPolicy: 'never' });
        session.turn = { id: 'turn', options };
        const request = { threadId: 'native-thread', turnId: 'turn', command: 'test' };
        assert.deepEqual(await session.serverRequest('item/commandExecution/requestApproval', request), { decision: 'decline' });
        assert.deepEqual(approvals, ['test']);
        session.turn.options = {};
        assert.deepEqual(await session.serverRequest('item/commandExecution/requestApproval', request), { decision: 'accept' });
        assert.equal(approvals.length, 1);
    });
}
