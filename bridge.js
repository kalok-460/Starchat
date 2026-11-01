// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
// Executes only in the user's official Spectrum page. No credentials leave this closure.
window.SpectrumProbe = (() => {
  'use strict';
  if (window.SpectrumProbe?.bridgeVersion === 2) window.SpectrumProbe.dispose();
  let uiLanguage = 'ko';
  const uiText = (ko, en) => uiLanguage === 'en' ? en : ko;
  let clientId = '';
  let selfMemberId = '';
  const chatSubscriptions = new Map();
  const readWaiters = new Map(), readAcks = new Map(), historyLimits = new Map();
  // Vite changes the content hash for every web deployment. Resolve only the
  // official Main module already referenced by this page, then check its API.
  // Verified store export: pr as ay in 7.160.0 (Main.uX02y0Mw.js) and the prior build.
  const OFFICIAL_MAIN = /^https:\/\/static\.robertsspaceindustries\.com\/spectrum\/static\/Main\.[A-Za-z0-9_-]{6,64}\.js$/;
  let eventSocket = null, eventHandler = null, eventStarting = null, eventGeneration = 0;
  const receivedInvites = new Map();
  const error = (code, status = 0) => Object.assign(new Error(code), { code, status });
  const id = value => {
    const n = Number(value);
    if (!Number.isSafeInteger(n) || n <= 0) throw error('INVALID_ID');
    return n;
  };
  const str = (value, max = 200) => String(value == null ? '' : value).slice(0, max);
  const required = (value, max) => {
    const s = str(value, max + 1).trim();
    if (!s || s.length > max) throw error('INVALID_TEXT');
    return s;
  };
  const array = (data, key) => {
    if (Array.isArray(data)) return data;
    if (data && Array.isArray(data[key])) return data[key];
    throw error('UNEXPECTED_RESPONSE');
  };
  const member = m => ({
    id: id(m.memberId || m.id), nickname: str(m.nickname),
    displayname: str(m.displayname || m.displayName), playerId: str(m.playerId),
    accountId: str(m.accountId), avatar: safeAvatar(m.avatar || m.avatarUrl),
    presence: str(m.presence?.status || m.status, 40)
  });
  // 호스트에는 공식 CDN의 이미지 URL만 전달한다. 임의 URL/로컬 파일은 열지 않는다.
  function safeAvatar(value) {
    try {
      const u = new URL(String(value || ''), 'https://robertsspaceindustries.com');
      return u.protocol === 'https:' && !u.username && !u.password &&
        (u.hostname === 'robertsspaceindustries.com' || u.hostname.endsWith('.robertsspaceindustries.com')) ? u.href : '';
    } catch { return ''; }
  }
  const lobby = (m, type) => {
    const lobbyId = id(m.id);
    // Subscription keys stay in the page. They are never projected into host results.
    if (typeof m.subscriptionKey === 'string' && m.subscriptionKey.length <= 512 &&
        m.subscriptionKey.length > 0 && !/[\s\x00-\x1f]/.test(m.subscriptionKey) && chatSubscriptions.size < 1000) {
      chatSubscriptions.set(String(lobbyId), m.subscriptionKey);
      subscribeKnownChats();
    }
    const unread = Number(m.newMessagesCount);
    return { id: lobbyId, name: str(m.name || m.member?.nickname), type,
      unread: Number.isSafeInteger(unread) && unread >= 0 ? Math.min(unread, 1000000) : 0,
      hasNewActivity: m.hasNewActivity === true || m.hasNewActivity === 1 };
  };
  function eventPageAllowed() {
    return location.origin === 'https://robertsspaceindustries.com' &&
      (location.pathname === '/spectrum' || location.pathname.startsWith('/spectrum/'));
  }
  function checkEventSession() {
    if (!eventPageAllowed()) throw error('OPEN_SPECTRUM_FIRST');
    const config = window.Tavern?.config;
    if (config?.api_root !== '/api/spectrum' || !/^[A-Za-z0-9-]{1,80}$/.test(config?.session_name || '')) throw error('PAGE_NOT_READY');
    if (!clientId || !selfMemberId || !document.cookie.split(';').some(x => x.trim().startsWith(config.session_name + '='))) throw error('LOGIN_REQUIRED');
    if (typeof window.chrome?.webview?.postMessage !== 'function') throw error('EVENTS_HOST_UNAVAILABLE');
  }
  function officialMainModule() {
    const unique = values => [...new Set(values.filter(value => typeof value === 'string' && OFFICIAL_MAIN.test(value)))];
    const elements = typeof document.querySelectorAll === 'function' ?
      [...document.querySelectorAll('link[rel="modulepreload"][href],script[type="module"][src]')].map(x => x.href || x.src) : [];
    const current = unique(elements);
    // DOM references describe the current boot module. Old performance entries
    // can remain after page-side navigation; do not let them override the DOM.
    if (current.length === 1) return current[0];
    if (current.length > 1) throw error('EVENTS_AMBIGUOUS_BUNDLE');
    const resources = typeof performance?.getEntriesByType === 'function' ?
      performance.getEntriesByType('resource').map(x => x.name) : [];
    const loaded = unique(resources);
    if (loaded.length === 1) return loaded[0];
    throw error(loaded.length > 1 ? 'EVENTS_AMBIGUOUS_BUNDLE' : 'EVENTS_UNSUPPORTED_BUNDLE');
  }
  function postEvent(value) {
    if (!eventPageAllowed()) return;
    try { window.chrome?.webview?.postMessage({ spectrumEvent: value }); } catch { /* Navigation/host shutdown. */ }
  }
  function subscribeKnownChats() {
    if (!eventSocket) return;
    const existing = eventSocket.getSessionSubscriptions();
    const needed = [...chatSubscriptions.values()].filter(key => !existing[key + ':update'])
      .map(subscriptionKey => ({ subscriptionKey, scope: 'update' }));
    if (needed.length) eventSocket.subscribe(needed);
  }
  function eventId(value) {
    if (typeof value === 'number' && (!Number.isSafeInteger(value) || value <= 0)) return '';
    if (typeof value !== 'string' && typeof value !== 'number' && typeof value !== 'bigint') return '';
    const s = String(value);
    return /^[1-9]\d{0,39}$/.test(s) ? s : '';
  }
  function finishRead(lobbyId, messageId) {
    const previous = readAcks.get(lobbyId);
    if (!previous || BigInt(messageId) > BigInt(previous)) readAcks.set(lobbyId, messageId);
    const wait = readWaiters.get(lobbyId);
    if (wait && BigInt(messageId) >= BigInt(wait.messageId)) {
      clearTimeout(wait.timer); clearTimeout(wait.verifyTimer); readWaiters.delete(lobbyId);
      wait.resolve({ acknowledged: true, messageId });
    }
    postEvent({ type: 'read', lobbyId, messageId });
  }
  async function verifyRead(lobbyId, wait) {
    if (readWaiters.get(lobbyId) !== wait || wait.verifying) return;
    wait.verifying = true; clearTimeout(wait.verifyTimer);
    try {
      checkEventSession();
      // 공식 소켓의 JSON.parse 단계에서 큰 메시지 ID가 이미 반올림될 수 있다.
      // 이때 해당 방의 서버 저장 last_read를 원문 JSON에서 정확히 읽어 확인한다.
      // sendEvent 호출 성공이나 미읽음 숫자 0만으로 서버 읽음 성공을 판단하지 않는다.
      const data = await request('/lobby/info', { lobby_id: id(lobbyId) });
      if (readWaiters.get(lobbyId) !== wait || wait.generation !== eventGeneration) return;
      checkEventSession();
      if (eventId(data?.id) !== lobbyId) return;
      const cursor = eventId(data.last_read);
      if (cursor && BigInt(cursor) >= BigInt(wait.messageId)) finishRead(lobbyId, cursor);
    } catch { /* A failed/old read-only check cannot acknowledge a write. */ }
  }
  function stopEvents() {
    for (const wait of readWaiters.values()) { clearTimeout(wait.timer); clearTimeout(wait.verifyTimer); wait.reject(error('READ_SESSION_CLOSED')); }
    readWaiters.clear(); readAcks.clear(); historyLimits.clear();
    eventGeneration++;
    if (eventSocket && eventHandler) eventSocket.removeMessageHandler(eventHandler);
    eventSocket = null; eventHandler = null; eventStarting = null;
    window.removeEventListener?.('pagehide', stopEvents);
    // Shared session subscriptions belong to the official page, which may have adopted
    // them meanwhile. Removing them would break the site's own sidebar. Only our listener
    // is removed; the official socket and its reconnect engine remain page-owned.
    return { active: false };
  }
  async function startEvents() {
    checkEventSession();
    if (eventSocket) { subscribeKnownChats(); return { active: true, mode: 'official-page-events', transport: 'page-managed' }; }
    if (eventStarting) return eventStarting;
    const generation = ++eventGeneration;
    eventStarting = (async () => {
      const moduleUrl = officialMainModule();
      // Import reuses the page's ES-module instance. No bundle fetching/eval,
      // arbitrary export discovery, second socket, or authentication is created.
      let module;
      try { module = await import(moduleUrl); }
      catch { throw error('EVENTS_MODULE_UNAVAILABLE'); }
      if (generation !== eventGeneration) throw error('EVENTS_START_CANCELLED');
      checkEventSession();
      const store = module.ay;
      if (typeof store?.getState !== 'function') throw error('EVENTS_UNSUPPORTED_CONTRACT');
      const state = store.getState();
      const pageMemberId = eventId(state?.identityInfos?.memberId);
      if (!pageMemberId) throw error('EVENTS_PAGE_NOT_READY');
      if (pageMemberId !== selfMemberId) throw error('EVENTS_ACCOUNT_CHANGED');
      const socket = state?.spectrumWsClient;
      if (!socket || !['addMessageHandler','removeMessageHandler','getSessionSubscriptions','subscribe','sendEvent'].every(k => typeof socket[k] === 'function')) throw error('EVENTS_PAGE_NOT_READY');
      const handler = event => {
        if (generation !== eventGeneration || !eventPageAllowed()) return;
        try { checkEventSession(); } catch { stopEvents(); return; }
        const pageMemberId = eventId(store.getState()?.identityInfos?.memberId);
        if (pageMemberId !== selfMemberId) { stopEvents(); return; }
        if (event?.type !== 'message' || !Array.isArray(event.detail) || event.detail.length > 200) return;
        for (const item of event.detail) {
          if (!item || typeof item !== 'object') continue;
          if (item.type === 'chat.direct_message.message.new' || item.type === 'chat.group_message.message.new') {
            const lobbyId = eventId(item.message?.lobbyId);
            if (!lobbyId || !Number.isSafeInteger(Number(lobbyId))) continue;
            // Fetch recent history for this room on demand to resolve member name and
            // retain precise IDs. Never forward arbitrary event payloads or raw tokens.
            postEvent({ type: 'message', lobbyId, messageId: eventId(item.message?.id), memberId: eventId(item.message?.memberId) });
          } else if (item.type === 'message_lobby.latest.update') {
            const lobbyId = eventId(item.lobbyId);
            if (lobbyId && chatSubscriptions.has(lobbyId)) postEvent({ type: 'message', lobbyId, messageId: eventId(item.messageId), memberId: '' });
          } else if (item.type === 'message_lobby.read.update') {
            // 공식 xar의 새 형식은 camelCase, Vp의 이전 형식은 data 안의
            // snake_case 필드다. 두 형식 모두 현재 저장된 공식 번들에서 확인했다.
            const lobbyId = eventId(item.lobbyId ?? item.data?.lobby_id);
            const messageId = eventId(item.messageId ?? item.data?.message_id);
            // Follow the official account-scoped read update, including reads
            // from another session. The official client does not filter owner.
            if (!lobbyId || !chatSubscriptions.has(lobbyId)) continue;
            if (messageId) finishRead(lobbyId, messageId);
            else { const wait = readWaiters.get(lobbyId); if (wait) void verifyRead(lobbyId, wait); }
          } else if (['chat.group_message.new','chat.group_message.update','private_lobbies.update','invite.accept','message_lobby.leave'].includes(item.type)) {
            postEvent({ type: 'rooms-changed' });
          } else if (item.type === 'reload') {
            postEvent({ type: 'error', code: 'EVENTS_PAGE_RELOAD' });
          }
        }
      };
      eventSocket = socket; eventHandler = handler;
      try { socket.addMessageHandler(handler); subscribeKnownChats(); }
      catch { stopEvents(); throw error('EVENTS_SUBSCRIBE_FAILED'); }
      window.addEventListener?.('pagehide', stopEvents);
      postEvent({ type: 'ready', mode: 'official-page-events', transport: 'page-managed' });
      return { active: true, mode: 'official-page-events', transport: 'page-managed' };
    })();
    try { return await eventStarting; }
    finally { if (generation === eventGeneration) eventStarting = null; }
  }
  function messageText(m) {
    for (const key of ['plaintext', 'text']) {
      if (typeof m[key] === 'string' && m[key].length) return { text: str(m[key], 12000), format: key };
    }
    for (const key of ['content_state', 'contentState', 'content']) {
      let raw = m[key];
      if (typeof raw === 'string' && raw.length <= 200000) {
        try { raw = JSON.parse(raw); } catch { if (key === 'content') return { text: str(raw, 12000), format: 'content-string' }; }
      }
      if (raw && Array.isArray(raw.blocks) && raw.blocks.every(b => b && typeof b.text === 'string')) {
        return { text: str(raw.blocks.map(b => b.text).join('\n'), 12000), format: 'draft-blocks' };
      }
    }
    if (m.media || m.media_id) return { text: uiText('[첨부/미디어 메시지]', '[Attachment / media message]'), format: 'media' };
    if (m.is_erased) return { text: uiText('[삭제된 메시지]', '[Deleted message]'), format: 'erased' };
    if (m.plaintext === '' || m.text === '') return { text: '', format: 'empty' };
    return { text: uiText('[지원되지 않는 본문 형식 — 진단 저장 후 알려주세요]', '[Unsupported message format]'), format: 'unknown' };
  }
  function jwtPayload(token) {
    try {
      const p = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
      return JSON.parse(atob(p.padEnd(Math.ceil(p.length / 4) * 4, '=')));
    } catch { return {}; }
  }
  const flag = x => x === true || x === 1 || x === '1' ? true : x === false || x === 0 || x === '0' ? false : null;
  const failure = e => ({ code: typeof e.code === 'string' && /^[A-Z_]+$/.test(e.code) ? e.code : 'UNEXPECTED_RESPONSE', status: Number(e.status) || 0, serverCode: e.serverCode || '', category: e.category || 'unknown' });
  function apiFailure(json, status) {
    const e = error('API_REJECTED', status);
    // Only numeric application codes and fixed classifications cross the bridge.
    e.serverCode = /^-?\d{1,10}$/.test(String(json?.code)) ? String(json.code) : '';
    const msg = typeof json?.msg === 'string' ? json.msg.toLowerCase() : '';
    e.category = /not.*(?:in|have).*party|no (?:active )?party/.test(msg) ? 'no-party' :
      /disabled|not enabled|unavailable|not implemented/.test(msg) ? 'unavailable' :
      /unauthori|forbidden|permission|access denied/.test(msg) ? 'permission' :
      /login|logged in|session|authenticat/.test(msg) ? 'authentication' :
      /invalid|validation|parameter/.test(msg) ? 'validation' : 'unknown';
    return e;
  }
  async function request(path, params = {}, options = {}) {
    if (location.origin !== 'https://robertsspaceindustries.com' ||
        !(location.pathname === '/spectrum' || location.pathname.startsWith('/spectrum/'))) throw error('OPEN_SPECTRUM_FIRST');
    const config = window.Tavern?.config;
    if (!config || config.api_root !== '/api/spectrum' ||
        !/^[A-Za-z0-9-]{1,80}$/.test(config.session_name || '')) throw error('PAGE_NOT_READY');
    const name = config.session_name;
    const cookie = document.cookie.split(';').map(x => x.trim()).find(x => x.startsWith(name + '='));
    if (!cookie) throw error('LOGIN_REQUIRED');
    let token;
    try { token = decodeURIComponent(cookie.slice(name.length + 1)); }
    catch { throw error('LOGIN_REQUIRED'); }
    const headers = { Accept: 'application/json', 'Content-Type': 'application/json', ['X-' + name]: token };
    if (clientId && !options.webParty) headers['x-tavern-id'] = clientId;
    const ctrl = new AbortController();
    const timeout = setTimeout(() => ctrl.abort(), 15000);
    try {
      const response = await fetch('/api/spectrum' + path, {
        method: 'POST', credentials: 'same-origin', redirect: 'error',
        headers, body: options.webParty || options.bodyless ? undefined : JSON.stringify(params), signal: ctrl.signal
      });
      if (!response.ok) throw error('HTTP_ERROR', response.status);
      let json;
      try {
        // 큰 메시지 ID가 Number로 반올림되기 전에 원문 정수 문자열을 보존한다(WebView2의 JSON reviver context).
        json = JSON.parse(await response.text(), (key, value, context) => {
          if (typeof value === 'number' && Number.isInteger(value) && !Number.isSafeInteger(value)) {
            if (context?.source && /^-?\d+$/.test(context.source)) return context.source;
            throw error('UNSAFE_INTEGER');
          }
          return value;
        });
      } catch (e) { throw error(e.code === 'UNSAFE_INTEGER' ? 'UNSAFE_INTEGER' : 'NOT_JSON'); }
      if (json?.success !== true && json?.success !== 1) throw apiFailure(json, response.status);
      if (!Object.prototype.hasOwnProperty.call(json, 'data')) throw error('UNEXPECTED_RESPONSE');
      return json.data;
    } catch (e) {
      if (e.code && typeof e.code === 'string') throw e;
      throw error(e.name === 'AbortError' ? 'TIMEOUT_RESULT_UNKNOWN' : 'NETWORK_RESULT_UNKNOWN');
    } finally { clearTimeout(timeout); }
  }
  async function identity() {
    const data = await request('/v2/getIdentityInfos');
    const spectrum = jwtPayload(data?.spectrumToken || '');
    const game = jwtPayload(data?.gameToken || '');
    const newMemberId = spectrum.member_id ? str(spectrum.member_id) : '';
    if (selfMemberId && selfMemberId !== newMemberId) { stopEvents(); chatSubscriptions.clear(); receivedInvites.clear(); }
    clientId = str(spectrum.client_id, 200);
    selfMemberId = newMemberId;
    return { anonymous: !spectrum.member_id, memberId: str(spectrum.member_id),
      accountId: str(game.account_id), playerId: str(game.player_id),
      partyFlag: flag(data?.featureFlags?.pf) };
  }
  const operations = {
    identity,
    startEvents,
    stopEvents,
    async self() {
      const data = await request('/auth/identify');
      return { member: data?.member?.id ? member(data.member) : null };
    },
    async friends() {
      const data = await request('/v2/relationship/friend/list', {}, { bodyless: true });
      return { members: array(data, 'members').map(member) };
    },
    async search(a) {
      const text = required(a.query, 100);
      if ([...text].length < 3) throw error('SEARCH_TOO_SHORT');
      // Matches the live web's Friend Add modal (bv.getParameters + Wvr).
      const data = await request('/search/member/autocomplete', { text, community_id: null, ignore_self: true });
      return { members: array(data, 'members').slice(0, 100).map(member), searchRoute: 'web-autocomplete' };
    },
    async profile(a) { return { member: member(await request('/v2/member/profile', { nickname: required(a.nickname, 100) })) }; },
    async friend(a) {
      await request('/v2/relationship/friend-request/create', { member_id: id(a.memberId) });
      return { acceptedByServer: true };
    },
    async chats(a) {
      const page = id(a.page || 1);
      const data = await request('/v2/lobby/chats/list', { page, pagesize: 30 });
      return { page, chats: array(data, 'chats').map(x => {
        if (!['group', 'private'].includes(x.type)) throw error('UNKNOWN_CHAT_TYPE');
        return lobby(x.data, x.type);
      }) };
    },
    async dm(a) {
      const data = await request('/v2/lobby/private/open', { member_nickname: required(a.nickname, 100) });
      return { chat: lobby(data, 'private') };
    },
    async party() {
      // Official legacy party UI gates itself on /auth/identify.data.pf.
      const diagnostics = { partyFlag: null, memberPresent: false, playerPresent: false, gameTokenPresent: false, gameTokenExpired: null };
      try {
        const auth = await request('/auth/identify');
        diagnostics.partyFlag = flag(auth?.pf);
        diagnostics.memberPresent = !!auth?.member?.id;
        diagnostics.playerPresent = !!auth?.playerId;
        diagnostics.gameTokenPresent = typeof auth?.gameToken === 'string' && auth.gameToken.length > 0;
        const exp = jwtPayload(auth?.gameToken || '').exp;
        if (typeof exp === 'number') diagnostics.gameTokenExpired = exp <= Date.now() / 1000;
      } catch (e) { diagnostics.identityFailure = failure(e); }
      try {
        // por({baseUrl:Lde,headers:Mde}).getParty() uses no body or client-id header.
        const data = await request('/v2/game/party', {}, { webParty: true });
        return { available: true, diagnostics, groupId: str(data?.id || data?.groupId), members: data ? array(data, 'members').map(member) : [] };
      } catch (e) { return { available: false, diagnostics, failure: failure(e), groupId: '', members: [] }; }
    },
    async invitations() {
      receivedInvites.clear();
      const data = await request('/auth/identify');
      const self = id(data?.member?.id);
      const notifications = Array.isArray(data.notifications) ? data.notifications : [];
      const invites = array(data, 'invites').filter(i => i?.status === 'pending' && Number(i.target_member_id) === self);
      const result = [];
      for (const i of invites) {
        if (typeof i.urn !== 'string' || !i.urn || i.urn.length > 512 || /[\s\x00-\x1f]/.test(i.urn)) continue;
        if (receivedInvites.has(i.urn)) continue;
        const n = notifications.find(n => n?.invite_urn === i.urn && n.type === 'group-lobby-invite');
        const parts = str(i.parent_urn, 512).split(':');
        const groupId = parts.length === 6 && /^\d+$/.test(parts[5]) ? parts[5] : '';
        const item = { urn: i.urn, groupId, name: str(n?.text_tokens?.lobby_name) || (groupId ? uiText('그룹 #', 'Group #') + groupId : uiText('그룹 이름 미제공', 'Unnamed group')), inviter: str(n?.text_tokens?.member_displayname) || uiText('회원 #', 'Member #') + str(i.requesting_member_id), status: 'pending' };
        receivedInvites.set(i.urn, item); result.push(item);
      }
      return { invitations: result };
    },
    async acceptInvitation(a) {
      if (typeof a.urn !== 'string' || !receivedInvites.has(a.urn)) throw error('RELOAD_INVITATIONS');
      await request('/lobby/acceptInvite', { invite_urn: a.urn });
      receivedInvites.delete(a.urn);
      return { acceptedByServer: true };
    },
    async createGroup(a) {
      const name = required(a.name, 80);
      if (!/^[a-z\d-]{1,80}$/.test(name)) throw error('INVALID_GROUP_NAME');
      const members = [...new Set(array(a, 'memberIds').map(id))];
      if (!members.length) throw error('SELECT_MEMBERS');
      const data = await request('/lobby/create', {
        type: 'group', name, media_id: null, members_ids: members
      });
      // Successful mutation with an unfamiliar response must never be auto-retried.
      const raw = data?.message_lobby || data?.lobby || data;
      let chat = null;
      try { if (raw?.id) chat = lobby(raw, 'group'); } catch { /* Creation already succeeded. */ }
      return { acceptedByServer: true, chat };
    },
    async invite(a) {
      const members = [...new Set(array(a, 'memberIds').map(id))];
      if (!members.length) throw error('SELECT_MEMBERS');
      await request('/lobby/invite', { lobby_id: id(a.lobbyId), member_ids: members });
      return { acceptedByServer: true };
    },
    async history(a) {
      const data = await request('/message/history', {
        lobby_id: id(a.lobbyId), timeframe: 'before', message_id: null, size: 50
      });
      const seen = new Set();
      const messages = array(data, 'messages').slice(0, 50).map(m => ({
        id: eventId(m.id), memberId: str(m.member_id || m.memberId || m.member?.id),
        nickname: str(m.member?.nickname || m.member?.displayname),
        ...messageText(m),
        createdAt: str(m.created_at ?? m.createdAt ?? m.time_created)
      })).filter(m => m.id && !seen.has(m.id) && seen.add(m.id));
      // Message ids are monotonic; avoid precision loss on large ids.
      messages.sort((a, b) => /^\d+$/.test(a.id) && /^\d+$/.test(b.id)
        ? (BigInt(a.id) < BigInt(b.id) ? -1 : BigInt(a.id) > BigInt(b.id) ? 1 : 0)
        : a.createdAt.localeCompare(b.createdAt));
      const formats = {};
      for (const m of messages) formats[m.format] = (formats[m.format] || 0) + 1;
      const latest=messages.length?eventId(messages[messages.length-1].id):'';
      const lobbyId=String(id(a.lobbyId)), previous=historyLimits.get(lobbyId);
      if(latest && (!previous || BigInt(latest)>BigInt(previous)))historyLimits.set(lobbyId,latest);
      return { messages, formats, unresolved: formats.unknown || 0 };
    },
    async markRead(a) {
      checkEventSession();
      const lobbyId=String(id(a.lobbyId)), messageId=eventId(a.messageId);
      const latest=historyLimits.get(lobbyId);
      if(!messageId || !latest || BigInt(messageId)>BigInt(latest))throw error('READ_MESSAGE_NOT_LOADED');
      await startEvents();
      if(!chatSubscriptions.has(lobbyId) || typeof eventSocket?.sendEvent!=='function')throw error('READ_TRANSPORT_UNAVAILABLE');
      const acknowledged=readAcks.get(lobbyId);
      if(acknowledged && BigInt(acknowledged)>=BigInt(messageId))return {acknowledged:true,messageId:acknowledged};
      if(readWaiters.has(lobbyId))throw error('READ_IN_PROGRESS');
      return await new Promise((resolve,reject)=>{
        const wait={messageId,resolve,reject,generation:eventGeneration,verifying:false,timer:null,verifyTimer:null};
        wait.timer=setTimeout(()=>{clearTimeout(wait.verifyTimer);readWaiters.delete(lobbyId);reject(error('READ_NOT_ACKNOWLEDGED'));},8000);
        readWaiters.set(lobbyId,wait);
        // Official client sends this WebSocket event, not an HTTP /read endpoint.
        // Keep message IDs as decimal strings; converting to Number loses precision.
        try{eventSocket.sendEvent({type:'message_lobby.read',lobby_id:id(a.lobbyId),message_id:messageId});}
        catch{clearTimeout(wait.timer);readWaiters.delete(lobbyId);reject(error('READ_SEND_FAILED'));return;}
        // 이미 같은 위치까지 읽은 경우에는 확인 이벤트가 다시 오지 않을 수 있다.
        // 방 메타데이터만 한 번 확인한다. 대화 내역의 주기 조회는 추가하지 않는다.
        if(readWaiters.get(lobbyId)===wait)wait.verifyTimer=setTimeout(()=>{void verifyRead(lobbyId,wait);},500);
      });
    },
    async send(a) {
      const text = required(a.text, 2000);
      const data = await request('/v2/message/send', { lobby_id: id(a.lobbyId), plaintext: text });
      // 공식 sendMessage 응답은 id/lobbyId/createdAt이다. 이 확정 ID로 즉시
      // 표시하면 이벤트나 직후 내역 조회가 늦어져도 자신의 발언이 빠지지 않는다.
      const messageId=eventId(data?.id), lobbyId=String(id(a.lobbyId)), previous=historyLimits.get(lobbyId);
      if(messageId && (!previous || BigInt(messageId)>BigInt(previous)))historyLimits.set(lobbyId,messageId);
      return { acceptedByServer: true, messageId, createdAt: str(data?.createdAt) };
    }
  };
  const bridge = async (operation, args, language) => {
    uiLanguage = language === 'en' ? 'en' : 'ko';
    try {
      if (!Object.prototype.hasOwnProperty.call(operations, operation)) throw error('UNKNOWN_OPERATION');
      return { ok: true, data: await operations[operation](args || {}) };
    } catch (e) {
      // No raw response, cookie, JWT, URL, or server message crosses the host bridge.
      return { ok: false, ...failure(e) };
    }
  };
  Object.defineProperties(bridge, { bridgeVersion: { value: 2 }, dispose: { value: stopEvents } });
  return bridge;
})();
