// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
const vm = require('node:vm');
const fs = require('node:fs');
const assert = require('node:assert/strict');
const test = require('node:test');
const source = fs.readFileSync(__dirname + '/bridge.js', 'utf8');
function harness(responses = [], overrides = {}) {
  const calls = [];
  const posts = [], imports = [], pageListeners = new Map();
  const socket = overrides.officialSocket || new OfficialSocket();
  const context = {
    window: { Tavern: { config: { api_root: '/api/spectrum', session_name: 'Rsi-Token' } },
      chrome:{webview:{postMessage:value=>posts.push(JSON.parse(JSON.stringify(value)))}},
      addEventListener:(name,fn)=>pageListeners.set(name,fn),
      removeEventListener:(name,fn)=>{if(pageListeners.get(name)===fn)pageListeners.delete(name);}
    },
    location: { origin: 'https://robertsspaceindustries.com', pathname: '/spectrum/community/SC' },
    document: { cookie: 'Rsi-Token=secret-cookie-value' },
    AbortController, setTimeout, clearTimeout, URL,
    performance:{getEntriesByType:()=>[{name:'https://static.robertsspaceindustries.com/spectrum/static/Main.uX02y0Mw.js'}]},
    __mockImport:async url=>{imports.push(url);return {ay:{getState:()=>({spectrumWsClient:socket,identityInfos:{memberId:12}})}};},
    atob: value => Buffer.from(value, 'base64').toString('binary'),
    fetch: async (url, options) => {
      calls.push({url, options, body: options.body === undefined ? undefined : JSON.parse(options.body)});
      const next = responses.shift();
      if (next instanceof Error) throw next;
      if (!next) throw Error('No mock response');
      return {ok: next.http == null || next.http === 200, status: next.http || 200,
        text: async () => next.rawText || JSON.stringify(next.raw || { success: true, data: next.data })};
    },
    ...overrides
  };
  // Controlled replay: replace only the ES-module import boundary. All production
  // origin checks, official named event callbacks, projections and lifecycle code run.
  const replaySource=source.replace('await import(moduleUrl)', 'await __mockImport(moduleUrl)');
  vm.runInNewContext(replaySource, context);
  return { call: async (op, args) => JSON.parse(JSON.stringify(await context.window.SpectrumProbe(op, args))), calls,
    posts,imports,socket,context,pageListeners,reinstall:()=>vm.runInNewContext(replaySource,context) };
}
class OfficialSocket extends EventTarget {
  constructor(){super();this.handlers=new Set();this.subscriptions={};this.subscriptionCalls=[];this.removals=0;this.sent=[];}
  sendEvent(event){this.sent.push(JSON.parse(JSON.stringify(event)));}
  addMessageHandler(fn){this.handlers.add(fn);this.addEventListener('message',fn);}
  removeMessageHandler(fn){this.handlers.delete(fn);this.removeEventListener('message',fn);this.removals++;}
  getSessionSubscriptions(){return this.subscriptions;}
  subscribe(values){this.subscriptionCalls.push(JSON.parse(JSON.stringify(values)));for(const v of values)this.subscriptions[v.subscriptionKey+':'+v.scope]=v;}
  emit(detail,type='message'){const event=new Event(type);Object.defineProperty(event,'detail',{value:detail});this.dispatchEvent(event);}
}
const identityResponse=()=>({data:{spectrumToken:'x.'+Buffer.from(JSON.stringify({member_id:12,client_id:'cid'})).toString('base64url')+'.secret-jwt'}});
test('send confirmation preserves exact server id and createdAt for immediate rendering', async()=>{
  const h=harness([{rawText:'{"success":true,"data":{"id":900719925474099301,"lobbyId":2,"createdAt":1761210000000,"secret":"not-exported"}}'}]);
  const result=await h.call('send',{lobbyId:2,text:'hello'});
  assert.equal(result.data.messageId,'900719925474099301');
  assert.equal(result.data.createdAt,'1761210000000');
  assert.equal(result.data.acceptedByServer,true);
  assert(!JSON.stringify(result).includes('not-exported'));
  assert.equal(h.calls.length,1);
});
test('no network call on load or unknown operation', async () => {
  const h = harness(); assert.equal(h.calls.length, 0);
  assert.equal((await h.call('constructor')).code, 'UNKNOWN_OPERATION');
  assert.equal(h.calls.length, 0);
});

test('friend list follows bodyless official route and only projects public display data', async () => {
  const jwt = x => 'x.' + Buffer.from(JSON.stringify(x)).toString('base64url') + '.x';
  const h = harness([{data:{spectrumToken:jwt({member_id:12,client_id:'cid'})}},
    {data:[{id:7,nickname:'Pilot',displayname:'파일럿',avatar:'https://cdn.robertsspaceindustries.com/a.jpg',presence:{status:'playing'},subscriptionKey:'secret-subscription'}]}]);
  await h.call('identity'); const r=await h.call('friends');
  assert.equal(h.calls[1].url,'/api/spectrum/v2/relationship/friend/list');
  assert.equal(h.calls[1].options.body,undefined);
  assert.equal(h.calls[1].options.headers['x-tavern-id'],'cid');
  assert.equal(r.data.members[0].presence,'playing');
  assert.equal(r.data.members[0].avatar,'https://cdn.robertsspaceindustries.com/a.jpg');
  assert(!JSON.stringify(r).includes('secret-subscription'));
});

test('self profile does not expose cookies tokens email and blocks foreign avatar URLs', async () => {
  const h=harness([{data:{member:{id:12,nickname:'Pilot',avatar:'https://evil.example/a.png',email:'private'},gameToken:'secret'}}]);
  const r=await h.call('self');assert.equal(r.data.member.nickname,'Pilot');assert.equal(r.data.member.avatar,'');
  assert(!JSON.stringify(r).includes('private'));assert(!JSON.stringify(r).includes('secret'));
});

test('unquoted large JSON message IDs remain exact at parsing boundary', async () => {
  const h=harness([{rawText:'{"success":true,"data":{"messages":[{"id":9007199254740993,"text":"one"},{"id":9007199254740992,"text":"two"}]}}'}]);
  const r=await h.call('history',{lobbyId:2});
  assert.deepEqual(r.data.messages.map(m=>m.id),['9007199254740992','9007199254740993']);
});
test('foreign origins and login page cannot make requests', async () => {
  for (const loc of [{origin:'https://evil.example',pathname:'/spectrum'}, {origin:'https://robertsspaceindustries.com',pathname:'/connect'}]) {
    const h = harness([], {location:loc});
    assert.equal((await h.call('identity')).code, 'OPEN_SPECTRUM_FIRST'); assert.equal(h.calls.length, 0);
  }
});
test('missing session and altered API root are rejected before fetch', async () => {
  let h = harness([], {document:{cookie:''}});
  assert.equal((await h.call('identity')).code, 'LOGIN_REQUIRED');
  h = harness([], {window:{Tavern:{config:{api_root:'https://evil.example',session_name:'Rsi-Token'}}}});
  assert.equal((await h.call('identity')).code, 'PAGE_NOT_READY'); assert.equal(h.calls.length,0);
});
test('identity emits ids only and keeps client header inside page', async () => {
  const jwt = x => 'eyJhbGciOiJIUzI1NiJ9.' + Buffer.from(JSON.stringify(x)).toString('base64url') + '.secret';
  const h = harness([{data:{spectrumToken:jwt({member_id:12,client_id:'cid'}),gameToken:jwt({account_id:34,player_id:56})}}, {data:{members:[]}}]);
  const r = await h.call('identity');
  assert.equal(r.data.memberId,'12'); assert.equal(r.data.accountId,'34');
  assert(!JSON.stringify(r).includes('Token')); assert(!JSON.stringify(r).includes('secret'));
  await h.call('search',{query:'Pilot'});
  assert.equal(h.calls[1].options.headers['x-tavern-id'],'cid');
  assert.equal(h.calls[1].options.credentials,'same-origin'); assert.equal(h.calls[1].options.redirect,'error');
});
test('member projection excludes arbitrary private fields', async () => {
  const h = harness([{data:{members:[{id:7,nickname:'Pilot',displayname:'파일럿',email:'hidden',token:'secret'}]}}]);
  const r = await h.call('search',{query:'Pilot'});
  assert.equal(r.data.members[0].nickname,'Pilot'); assert(!JSON.stringify(r).includes('hidden'));
  assert.equal(h.calls[0].url,'/api/spectrum/search/member/autocomplete');
  assert.deepEqual(h.calls[0].body,{text:'Pilot',community_id:null,ignore_self:true});
});
test('short partial search makes no server call; exact profile still permits short handles', async () => {
  const h=harness([{data:{id:3,nickname:'AB'}}]);
  assert.equal((await h.call('search',{query:'AB'})).code,'SEARCH_TOO_SHORT');
  assert.equal(h.calls.length,0);
  assert.equal((await h.call('profile',{nickname:'AB'})).data.member.nickname,'AB');
});
test('legacy history content_state Draft blocks preserve Korean, mentions and blank lines', async () => {
  const h=harness([{data:{messages:[{id:1,content_state:{blocks:[{text:'안녕 @Pilot'},{text:''},{text:'다음 줄 👋'}],entityMap:{secret:'not exported'}}}]}}]);
  const r=await h.call('history',{lobbyId:2});
  assert.equal(r.data.messages[0].text,'안녕 @Pilot\n\n다음 줄 👋');
  assert.equal(r.data.formats['draft-blocks'],1);assert.equal(r.data.unresolved,0);
  assert(!JSON.stringify(r).includes('not exported'));
});
test('serialized content_state and empty plaintext fall back to actual blocks', async () => {
  const h=harness([{data:{messages:[{id:1,plaintext:'',content_state:JSON.stringify({blocks:[{text:'내용'}]})}]}}]);
  assert.equal((await h.call('history',{lobbyId:2})).data.messages[0].text,'내용');
});
test('unsupported, attachment and deleted messages are distinguished without raw export', async () => {
  const h=harness([{data:{messages:[{id:1,content_state:{token:'hidden'}},{id:2,media:{id:3}},{id:3,is_erased:true}]}}]);
  const r=await h.call('history',{lobbyId:2});
  assert.equal(r.data.unresolved,1);assert.equal(r.data.formats.media,1);assert.equal(r.data.formats.erased,1);
  assert(!JSON.stringify(r).includes('hidden'));
});
test('friend click sends one specific member request without automatic retries', async () => {
  const h = harness([{http:403}]);
  const r = await h.call('friend',{memberId:7});
  assert.equal(r.status,403); assert.equal(h.calls.length,1);
  assert.equal(h.calls[0].url,'/api/spectrum/v2/relationship/friend-request/create');
  assert.deepEqual(h.calls[0].body,{member_id:7});
});
test('invalid member id never sends request', async () => {
  const h=harness();
  for(const memberId of ['Pilot',-1,0,1.2,'9007199254740993']) assert.equal((await h.call('friend',{memberId})).code,'INVALID_ID');
  assert.equal(h.calls.length,0);
});
test('group creation and invite use distinct ids/parameter names', async () => {
  const h=harness([{data:{message_lobby:{id:99,name:'korean-chat'}}},{data:{}}]);
  let r=await h.call('createGroup',{name:'korean-chat',memberIds:[3,3,4]});
  assert.equal(r.data.chat.id,99); assert.deepEqual(h.calls[0].body.members_ids,[3,4]);
  await h.call('invite',{lobbyId:99,memberIds:[4]});
  assert.deepEqual(h.calls[1].body,{lobby_id:99,member_ids:[4]});
});
test('group successful but unfamiliar response is not retried', async () => {
  const h=harness([{data:{newSchema:true}}]);
  const r=await h.call('createGroup',{name:'test',memberIds:[3]});
  assert.equal(r.data.acceptedByServer,true); assert.equal(r.data.chat,null); assert.equal(h.calls.length,1);
});
test('Unicode and multiline content survive sending', async () => {
  const h=harness([{data:{id:50}}]);
  const text='안녕하세요 👋\n파티 테스트';
  assert.equal((await h.call('send',{lobbyId:9,text})).ok,true);
  assert.equal(h.calls[0].body.plaintext,text);
  assert.equal(h.calls[0].url,'/api/spectrum/v2/message/send');
});
test('history deduplicates and sorts without BigInt precision loss', async () => {
  const h=harness([{data:{messages:[{id:'9007199254740993',member:{id:4,nickname:'B'},plaintext:'둘'}, {id:'9007199254740992',member_id:3,text:'하나'}, {id:'9007199254740993',text:'중복'}]}}]);
  const r=await h.call('history',{lobbyId:9});
  assert.equal(r.data.messages.length,2); assert.equal(r.data.messages[0].text,'하나');
  assert.equal(r.data.messages[1].nickname,'B'); assert.equal(h.calls[0].body.size,50);
});
test('party member id differs from player id', async () => {
  const h=harness([{data:{pf:true,member:{id:1}}},{data:{groupId:'party-1',members:[{memberId:11,playerId:'222',nickname:'Pilot'}]}},{data:{pf:false,member:{id:1}}},{data:null}]);
  let r=await h.call('party'); assert.equal(r.data.members[0].id,11); assert.equal(r.data.members[0].playerId,'222');
  r=await h.call('party'); assert.deepEqual(r.data.members,[]);
});
test('chat lists preserve private/group semantics and page', async () => {
  const h=harness([{data:[{type:'private',data:{id:1,name:'A'}},{type:'group',data:{id:2,name:'B'}}]}]);
  const r=await h.call('chats',{page:2}); assert.equal(r.data.chats[1].type,'group'); assert.equal(h.calls[0].body.page,2);
});
test('server denial excludes raw response and server secrets', async () => {
  const h=harness([{raw:{success:false,msg:'secret-cookie-value',data:{token:'sensitive'}}}]);
  const r=await h.call('identity'); assert.equal(r.code,'API_REJECTED'); assert(!JSON.stringify(r).includes('secret'));
});
test('network errors have unknown outcome and no retry', async () => {
  const h=harness([new Error('network contains token=secret')]);
  const r=await h.call('send',{lobbyId:2,text:'hello'});
  assert.equal(r.code,'NETWORK_RESULT_UNKNOWN'); assert.equal(h.calls.length,1); assert(!JSON.stringify(r).includes('secret'));
});
test('group names match web restrictions before sending; invites never send a room name', async () => {
  const h=harness([{data:{}}]);
  for(const name of ['한글','Test','a b','a_b','a'.repeat(81)]) assert.equal((await h.call('createGroup',{name,memberIds:[3]})).ok,false);
  assert.equal(h.calls.length,0);
  await h.call('invite',{lobbyId:5,memberIds:[3],name:'한글'});
  assert.deepEqual(h.calls[0].body,{lobby_id:5,member_ids:[3]});
});
test('party probes legacy pf rather than treating absent featureFlags as disabled; no bearer leaks', async () => {
  const jwt=x=>'a.'+Buffer.from(JSON.stringify(x)).toString('base64url')+'.secret';
  const h=harness([{data:{pf:'0',member:{id:2},playerId:6,gameToken:jwt({exp:1})}}, {raw:{success:0,code:403,msg:'Permission denied secret-account',data:{token:'secret'}}}]);
  const r=await h.call('party');
  assert.equal(r.data.available,false);assert.equal(r.data.diagnostics.partyFlag,false);
  assert.equal(r.data.diagnostics.gameTokenExpired,true);
  assert.equal(r.data.failure.serverCode,'403');assert.equal(r.data.failure.category,'permission');
  assert.equal(h.calls[1].body,undefined);assert.equal(h.calls[1].options.headers['x-tavern-id'],undefined);
  assert(!JSON.stringify(r).includes('secret'));assert.equal(h.calls.length,2);
});
test('missing party flag is unknown and failed identity probe does not fabricate an empty party', async()=>{
 const h=harness([{http:503},{http:403}]);const r=await h.call('party');
 assert.equal(r.data.diagnostics.partyFlag,null);assert.equal(r.data.available,false);
 assert.equal(r.data.diagnostics.identityFailure.status,503);assert.equal(r.data.failure.status,403);
});
function inviteData(){return {member:{id:7},gameToken:'secret-token',invites:[
 {urn:'urn:test:invite:1',parent_urn:'urn:test:spectrum:lobby:id:42',status:'pending',target_member_id:7,requesting_member_id:8},
 {urn:'urn:test:invite:2',status:'pending',target_member_id:8,requesting_member_id:7},
 {urn:'urn:test:invite:3',status:'accepted',target_member_id:7},
 ],notifications:[{type:'group-lobby-invite',invite_urn:'urn:test:invite:1',text_tokens:{lobby_name:'test-group',member_displayname:'파일럿',secret:'hidden'}}]};}
test('received invitations filter outgoing and completed entries, preserve labels, and do not accept on read',async()=>{
 const h=harness([{data:inviteData()}]);const r=await h.call('invitations');
 assert.equal(r.data.invitations.length,1);assert.equal(r.data.invitations[0].name,'test-group');
 assert.equal(r.data.invitations[0].inviter,'파일럿');assert.equal(r.data.invitations[0].groupId,'42');
 assert.equal(h.calls.length,1);assert.equal(h.calls[0].url,'/api/spectrum/auth/identify');
 assert(!JSON.stringify(r).includes('secret'));assert(!JSON.stringify(r).includes('hidden'));
});
test('accept uses only previously received invitation URN and cannot resend a success',async()=>{
 const h=harness([{data:inviteData()},{data:{}}]);
 assert.equal((await h.call('acceptInvitation',{urn:'made-up'})).code,'RELOAD_INVITATIONS');assert.equal(h.calls.length,0);
 await h.call('invitations');const r=await h.call('acceptInvitation',{urn:'urn:test:invite:1'});
 assert.equal(r.data.acceptedByServer,true);assert.deepEqual(h.calls[1].body,{invite_urn:'urn:test:invite:1'});
 assert.equal(h.calls[1].url,'/api/spectrum/lobby/acceptInvite');
 assert.equal((await h.call('acceptInvitation',{urn:'urn:test:invite:1'})).code,'RELOAD_INVITATIONS');assert.equal(h.calls.length,2);
});
test('failed refresh invalidates old invites; acceptance network failure is never automatically retried',async()=>{
 const h=harness([{data:inviteData()},new Error('offline'),new Error('offline')]);await h.call('invitations');
 assert.equal((await h.call('acceptInvitation',{urn:'urn:test:invite:1'})).code,'NETWORK_RESULT_UNKNOWN');assert.equal(h.calls.length,2);
 await h.call('invitations');assert.equal((await h.call('acceptInvitation',{urn:'urn:test:invite:1'})).code,'RELOAD_INVITATIONS');assert.equal(h.calls.length,3);
});

test('events require identity and supported already-loaded module; no speculative module imports',async()=>{
 const h=harness([identityResponse()]);assert.equal((await h.call('startEvents')).code,'LOGIN_REQUIRED');
 assert.equal(h.imports.length,0);await h.call('identity');
 h.context.performance.getEntriesByType=()=>[{name:'https://evil.example/spectrum/static/Main.VAuAukRU.js'}];
 assert.equal((await h.call('startEvents')).code,'EVENTS_UNSUPPORTED_BUNDLE');assert.equal(h.imports.length,0);
});

test('event startup follows the deployed Main hash instead of pinning an obsolete build',async()=>{
 for(const name of ['Main.VAuAukRU.js','Main.uX02y0Mw.js','Main.Next_123-hash.js']){
  const url='https://static.robertsspaceindustries.com/spectrum/static/'+name;
  const h=harness([identityResponse()],{performance:{getEntriesByType:()=>[{name:url},{name:url}]}});
  await h.call('identity');assert.equal((await h.call('startEvents')).data.active,true);
  assert.deepEqual(h.imports,[url]);assert.equal(h.calls.length,1);assert.equal(h.socket.handlers.size,1);
 }
});

test('current DOM module takes precedence over stale performance entries',async()=>{
 const url='https://static.robertsspaceindustries.com/spectrum/static/Main.uX02y0Mw.js';
 const h=harness([identityResponse()],{
  performance:{getEntriesByType:()=>[{name:'https://static.robertsspaceindustries.com/spectrum/static/Main.VAuAukRU.js'}]},
  document:{cookie:'Rsi-Token=private',querySelectorAll:()=>[{href:url},{src:url}]}
 });
 await h.call('identity');assert.equal((await h.call('startEvents')).data.active,true);
 assert.deepEqual(h.imports,[url]);
});

test('Main resolver rejects foreign, redirected-looking and ambiguous module references',async()=>{
 const current='https://static.robertsspaceindustries.com/spectrum/static/Main.uX02y0Mw.js';
 for(const url of [current+'?redirect=evil',current+'#fragment',current.replace('https:','http:'),
  current.replace('static.robertsspaceindustries.com','static.robertsspaceindustries.com.evil.example'),
  current.replace('/spectrum/static/','/other/'),current.replace('Main.','Other.'),
  current.replace('https://','https://user@')]){
  const h=harness([identityResponse()],{performance:{getEntriesByType:()=>[{name:url}]}});
  await h.call('identity');assert.equal((await h.call('startEvents')).code,'EVENTS_UNSUPPORTED_BUNDLE');
  assert.equal(h.imports.length,0);assert.equal(h.socket.handlers.size,0);
 }
 for(const dom of [false,true]){
  const urls=[current,current.replace('uX02y0Mw','VAuAukRU')];
  const overrides=dom?{document:{cookie:'Rsi-Token=private',querySelectorAll:()=>urls.map(href=>({href}))}}:
   {performance:{getEntriesByType:()=>urls.map(name=>({name}))}};
  const h=harness([identityResponse()],overrides);await h.call('identity');
  assert.equal((await h.call('startEvents')).code,'EVENTS_AMBIGUOUS_BUNDLE');assert.equal(h.imports.length,0);
 }
});

test('a changed export, missing identity or incomplete official socket fails before attaching',async()=>{
 const noSend=new OfficialSocket();noSend.sendEvent=undefined;
 for(const [module,expected] of [
  [{different:{getState:()=>({})}},'EVENTS_UNSUPPORTED_CONTRACT'],
  [{ay:{getState:()=>({spectrumWsClient:new OfficialSocket()})}},'EVENTS_PAGE_NOT_READY'],
  [{ay:{getState:()=>({identityInfos:{memberId:12},spectrumWsClient:noSend})}},'EVENTS_PAGE_NOT_READY']
 ]){
  const h=harness([identityResponse()],{__mockImport:async()=>module});await h.call('identity');
  assert.equal((await h.call('startEvents')).code,expected);assert.equal(h.posts.length,0);
 }
});

test('read synchronization works with the newly deployed module and still requires server confirmation',async()=>{
 const url='https://static.robertsspaceindustries.com/spectrum/static/Main.uX02y0Mw.js';
 const h=await readHarness({document:{cookie:'Rsi-Token=private',querySelectorAll:()=>[{href:url}]}});
 const wait=h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});
 await new Promise(resolve=>setImmediate(resolve));
 assert.deepEqual(h.imports,[url]);
 assert.deepEqual(h.socket.sent,[{type:'message_lobby.read',lobby_id:42,message_id:'900719925474099312'}]);
 h.socket.emit([{type:'message_lobby.read.update',owner:true,lobbyId:42,messageId:'900719925474099312'}]);
 assert.equal((await wait).data.acknowledged,true);
});
test('start events reuses official named emitter and is idempotent without opening any socket',async()=>{
 const h=harness([identityResponse()]);await h.call('identity');
 assert.equal((await h.call('startEvents')).data.active,true);await h.call('startEvents');
 assert.equal(h.imports.length,1);assert.equal(h.socket.handlers.size,1);assert.equal(h.calls.length,1);
 assert.deepEqual(h.posts,[{spectrumEvent:{type:'ready',mode:'official-page-events',transport:'page-managed'}}]);
});
test('official private/group event projection excludes raw payload secrets and keeps exact string ID',async()=>{
 const h=harness([identityResponse()]);await h.call('identity');await h.call('startEvents');h.posts.length=0;
 h.socket.emit([{type:'chat.direct_message.message.new',owner:false,message:{id:'9007199254740993',lobbyId:42,memberId:7,text:'private-message-not-forwarded',token:'secret',subscriptionKey:'private-key'}},
 {type:'chat.group_message.message.new',message:{id:99,lobbyId:43,memberId:8}}]);
 assert.deepEqual(h.posts,[{spectrumEvent:{type:'message',lobbyId:'42',messageId:'9007199254740993',memberId:'7'}},{spectrumEvent:{type:'message',lobbyId:'43',messageId:'99',memberId:'8'}}]);
 assert(!JSON.stringify(h.posts).includes('secret'));assert(!JSON.stringify(h.posts).includes('private'));
});
test('malformed, public, raw-wire and foreign-origin events are not forwarded',async()=>{
 const h=harness([identityResponse()]);await h.call('identity');await h.call('startEvents');h.posts.length=0;
 h.socket.emit(null);h.socket.emit({type:'message'});h.socket.emit(new Array(201).fill({}));
 h.socket.emit([{type:'message.new',payloadV2:{message:{lobbyId:42}}},{type:'chat.lobby.message.new',message:{id:9,lobbyId:42}},
 {type:'chat.group_message.message.new',message:{lobbyId:'invalid'}},{type:'chat.group_message.message.new',message:{lobbyId:9007199254740992}}]);
 h.socket.emit([{type:'chat.group_message.message.new',message:{lobbyId:42}}],'wrong-event');
 h.context.location.origin='https://evil.example';h.socket.emit([{type:'chat.group_message.message.new',message:{id:9,lobbyId:42}}]);
 assert.equal(h.posts.length,0);
});
test('chat list exposes server unread baseline and keeps subscription keys inside official page',async()=>{
 const h=harness([identityResponse(),{data:[{type:'private',data:{id:42,name:'DM',subscriptionKey:'secret-room-key',newMessagesCount:7,hasNewActivity:true}}]}]);
 await h.call('identity');await h.call('startEvents');const chats=await h.call('chats',{page:1});
 assert.equal(chats.data.chats[0].unread,7);assert.equal(chats.data.chats[0].hasNewActivity,true);
 assert(!JSON.stringify(chats).includes('secret-room-key'));
 assert.deepEqual(h.socket.subscriptionCalls,[[{subscriptionKey:'secret-room-key',scope:'update'}]]);
 await h.call('startEvents');assert.equal(h.socket.subscriptionCalls.length,1);
});
test('known room latest/read updates and room changes emit bounded host signals',async()=>{
 const h=harness([identityResponse(),{data:[{type:'group',data:{id:42,name:'Group',subscriptionKey:'room-key'}}]}]);
 await h.call('identity');await h.call('chats');await h.call('startEvents');h.posts.length=0;
 h.socket.emit([{type:'message_lobby.latest.update',lobbyId:42,messageId:99},{type:'message_lobby.read.update',lobbyId:42,messageId:99},
 {type:'message_lobby.latest.update',lobbyId:999,messageId:100},{type:'private_lobbies.update',data:{token:'secret'}},
 {type:'reload',message:'raw secret'}]);
 assert.deepEqual(h.posts,[{spectrumEvent:{type:'message',lobbyId:'42',messageId:'99',memberId:''}},{spectrumEvent:{type:'read',lobbyId:'42',messageId:'99'}},
 {spectrumEvent:{type:'rooms-changed'}},{spectrumEvent:{type:'error',code:'EVENTS_PAGE_RELOAD'}}]);
});

async function readHarness(overrides={},verificationResponses=[]){
 const h=harness([identityResponse(),{data:[{type:'group',data:{id:42,name:'Read fixture',subscriptionKey:'read-key'}}]},
 {data:{messages:[{id:'900719925474099312',member:{nickname:'Pilot'},plaintext:'Test'}]}},...verificationResponses],overrides);
 await h.call('identity');await h.call('chats');await h.call('history',{lobbyId:42});await h.call('startEvents');return h;
}
test('accepted own message remains readable when a stale history response arrives later',async()=>{
 const h=await readHarness({},[{data:{id:'900719925474099313',lobbyId:42,createdAt:'1761210000000'}},
   {data:{messages:[{id:'900719925474099312',member:{nickname:'Pilot'},plaintext:'Older snapshot'}]}}]);
 const sent=await h.call('send',{lobbyId:42,text:'My accepted text'});
 assert.equal(sent.data.messageId,'900719925474099313');
 await h.call('history',{lobbyId:42});
 const wait=h.call('markRead',{lobbyId:42,messageId:sent.data.messageId});
 await new Promise(resolve=>setImmediate(resolve));
 h.socket.emit([{type:'message_lobby.read.update',lobbyId:42,messageId:sent.data.messageId}]);
 assert.equal((await wait).data.acknowledged,true);
 assert.equal(h.calls.filter(x=>x.url.endsWith('/v2/message/send')).length,1);
});
test('read cursor uses official socket and waits for server acknowledgment, preserving big IDs',async()=>{
 const h=await readHarness();let done=false;
 const wait=h.call('markRead',{lobbyId:42,messageId:'900719925474099312'}).then(r=>{done=true;return r});
 await new Promise(resolve=>setImmediate(resolve));assert.equal(done,false);
 assert.deepEqual(h.socket.sent,[{type:'message_lobby.read',lobby_id:42,message_id:'900719925474099312'}]);
 h.socket.emit([{type:'message_lobby.read.update',lobbyId:42,messageId:'900719925474099311'}]);
 await new Promise(resolve=>setImmediate(resolve));assert.equal(done,false);
 h.socket.emit([{type:'message_lobby.read.update',owner:false,lobbyId:42,messageId:'900719925474099312'}]);
 assert.equal((await wait).data.acknowledged,true);
 assert.equal((await h.call('markRead',{lobbyId:42,messageId:'900719925474099312'})).data.acknowledged,true);
 assert.equal(h.socket.sent.length,1);assert.equal(h.calls.length,3);
});
test('read cannot mark messages not actually loaded and rejects duplicate in-flight requests',async()=>{
 const h=await readHarness();assert.equal((await h.call('markRead',{lobbyId:42,messageId:'900719925474099313'})).code,'READ_MESSAGE_NOT_LOADED');
 assert.equal((await h.call('markRead',{lobbyId:43,messageId:'1'})).code,'READ_MESSAGE_NOT_LOADED');
 const wait=h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});await new Promise(resolve=>setImmediate(resolve));
 assert.equal((await h.call('markRead',{lobbyId:42,messageId:'900719925474099312'})).code,'READ_IN_PROGRESS');
 await h.call('stopEvents');assert.equal((await wait).code,'READ_SESSION_CLOSED');
});
test('disconnected socket does not pretend that read has reached server',async()=>{
 const h=await readHarness({setTimeout:(fn,ms)=>setTimeout(fn,ms===8000?20:ms)});
 const result=await h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});
 assert.equal(result.ok,false);assert.equal(result.code,'READ_NOT_ACKNOWLEDGED');assert.equal(h.socket.sent.length,1);
});
test('malformed or unknown read updates cannot clear room state',async()=>{
 const h=await readHarness();h.posts.length=0;
 h.socket.emit([{type:'message_lobby.read.update',lobbyId:42},{type:'message_lobby.read.update',lobbyId:99,messageId:'55'},
 {type:'message_lobby.read.update',lobbyId:42,messageId:9007199254740992}]);assert.equal(h.posts.length,0);
});

const fastReadTimers={setTimeout:(fn,ms)=>setTimeout(fn,ms===500?5:ms===8000?80:ms)};
test('already-current cursor without another websocket echo is confirmed from server metadata once',async()=>{
 const h=await readHarness(fastReadTimers,[{rawText:'{"success":true,"data":{"id":42,"last_read":900719925474099312}}'}]);
 h.posts.length=0;const r=await h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});
 assert.equal(r.data.acknowledged,true);assert.equal(r.data.messageId,'900719925474099312');
 assert.equal(h.socket.sent.length,1);assert.equal(h.calls.length,4);
 assert.equal(h.calls[3].url,'/api/spectrum/lobby/info');assert.deepEqual(h.calls[3].body,{lobby_id:42});
 assert.equal(h.calls.filter(x=>x.url.endsWith('/message/history')).length,1);
 assert.deepEqual(h.posts,[{spectrumEvent:{type:'read',lobbyId:'42',messageId:'900719925474099312'}}]);
});
test('official JSON.parse rounded v2 read cursor is only a hint; precise HTTP cursor acknowledges it',async()=>{
 const h=await readHarness({},[{rawText:'{"success":true,"data":{"id":42,"last_read":900719925474099312}}'}]);
 const wait=h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});await new Promise(resolve=>setImmediate(resolve));
 // Rar.parseIncomingMessage in the shipped module uses ordinary JSON.parse,
 // then xar projects payloadV2.messageId unchanged into the domain event.
 const raw=JSON.parse('{"type":"message_lobby.read.update","payloadV2":{"lobbyId":42,"messageId":900719925474099312}}');
 assert(!Number.isSafeInteger(raw.payloadV2.messageId));
 h.socket.emit([{type:raw.type,owner:false,...raw.payloadV2}]);
 const r=await wait;assert.equal(r.data.messageId,'900719925474099312');assert.equal(h.calls.length,4);
});
test('official legacy Vp domain shape acknowledges exact read cursor without a metadata fetch',async()=>{
 const h=await readHarness();const wait=h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});
 await new Promise(resolve=>setImmediate(resolve));
 h.socket.emit([{type:'message_lobby.read.update',owner:true,data:{lobby_id:42,message_id:'900719925474099312',client_id:'private'}}]);
 assert.equal((await wait).data.acknowledged,true);assert.equal(h.calls.length,3);assert(!JSON.stringify(h.posts).includes('private'));
});
test('rounded legacy read cursor also checks stored server position and coalesces duplicate hints',async()=>{
 const h=await readHarness({},[{data:{id:42,last_read:'900719925474099312'}}]);
 const wait=h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});await new Promise(resolve=>setImmediate(resolve));
 const raw=JSON.parse('{"type":"message_lobby.read.update","lobby_id":42,"message_id":900719925474099312}');
 const {type,...data}=raw;h.socket.emit([{type,data},{type,data},{type,data}]);
 assert.equal((await wait).data.acknowledged,true);assert.equal(h.calls.length,4);
});
test('read verification rejects an older position, another room, or unread counter without a cursor',async()=>{
 for(const data of [{id:42,last_read:'900719925474099311',new_messages:0},{id:43,last_read:'900719925474099312'},{id:42,new_messages:0},
 {id:42,latest:'900719925474099312'},{id:42,last_read:null,latest:'900719925474099312'},{id:42,last_read:'0',latest:'900719925474099312'}]){
  const h=await readHarness(fastReadTimers,[{data}]);h.posts.length=0;
  const r=await h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});
  assert.equal(r.code,'READ_NOT_ACKNOWLEDGED');assert.equal(h.calls.length,4);assert.equal(h.posts.length,0);
 }
});
test('failed metadata verification never converts a disconnected write into success',async()=>{
 const h=await readHarness(fastReadTimers,[new Error('network-failed')]);
 const r=await h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});
 assert.equal(r.code,'READ_NOT_ACKNOWLEDGED');assert.equal(h.calls.length,4);assert.equal(h.socket.sent.length,1);
});
test('HTTP and application denials cannot acknowledge a read even if response includes a cursor',async()=>{
 for(const response of [{http:403,data:{id:42,last_read:'900719925474099312'}},
 {raw:{success:false,code:403,msg:'private-response-not-forwarded',data:{id:42,last_read:'900719925474099312'}}}]){
  const h=await readHarness(fastReadTimers,[response]);h.posts.length=0;
  const r=await h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});
  assert.equal(r.code,'READ_NOT_ACKNOWLEDGED');assert.equal(h.posts.length,0);assert(!JSON.stringify(r).includes('private-response'));
 }
});
test('late metadata response after session close cannot clear unread state of a new account',async()=>{
 const h=await readHarness(fastReadTimers);let release;h.posts.length=0;
 h.context.fetch=()=>new Promise(resolve=>{release=resolve});
 const wait=h.call('markRead',{lobbyId:42,messageId:'900719925474099312'});
 while(!release)await new Promise(resolve=>setTimeout(resolve,1));
 await h.call('stopEvents');assert.equal((await wait).code,'READ_SESSION_CLOSED');
 release({ok:true,status:200,text:async()=>'{"success":true,"data":{"id":42,"last_read":"900719925474099312"}}'});
 await new Promise(resolve=>setImmediate(resolve));assert.equal(h.posts.length,0);
});
test('stop unsubscribes only our listener, preserves shared subscriptions and ignores late callbacks',async()=>{
 const h=harness([identityResponse(),{data:[{type:'group',data:{id:42,subscriptionKey:'shared-key'}}]}]);await h.call('identity');await h.call('chats');await h.call('startEvents');
 const late=[...h.socket.handlers][0];h.posts.length=0;assert.equal((await h.call('stopEvents')).data.active,false);
 assert.equal(h.socket.handlers.size,0);assert.equal(h.pageListeners.size,0);assert(h.socket.subscriptions['shared-key:update']);
 late({type:'message',detail:[{type:'chat.group_message.message.new',message:{id:9,lobbyId:42}}]});assert.equal(h.posts.length,0);
 await h.call('startEvents');assert.equal(h.socket.handlers.size,1);assert.equal(h.socket.subscriptionCalls.length,1);
});
test('pagehide and reinjection clean up old emitter handlers',async()=>{
 const h=harness([identityResponse(),identityResponse()]);await h.call('identity');await h.call('startEvents');
 h.pageListeners.get('pagehide')();assert.equal(h.socket.handlers.size,0);await h.call('startEvents');
 h.reinstall();assert.equal(h.socket.handlers.size,0);await h.call('identity');await h.call('startEvents');assert.equal(h.socket.handlers.size,1);
});
test('stop during module import prevents stale start from attaching after logout',async()=>{
 let resolveImport;const pending=new Promise(resolve=>{resolveImport=resolve});const socket=new OfficialSocket();
 const h=harness([identityResponse()],{__mockImport:()=>pending});await h.call('identity');const start=h.call('startEvents');
 await h.call('stopEvents');resolveImport({ay:{getState:()=>({spectrumWsClient:socket,identityInfos:{memberId:12}})}});
 assert.equal((await start).code,'EVENTS_START_CANCELLED');assert.equal(socket.handlers.size,0);assert.equal(h.posts.length,0);
});
test('missing shared socket, changed account and import failures cannot pretend ready',async()=>{
 for(const state of [{},{spectrumWsClient:new OfficialSocket(),identityInfos:{memberId:99}}]){
  const h=harness([identityResponse()],{__mockImport:async()=>({ay:{getState:()=>state}})});await h.call('identity');
  assert.equal((await h.call('startEvents')).ok,false);assert.equal(h.posts.length,0);
 }
 const h=harness([identityResponse()],{__mockImport:async()=>{throw Error('token=secret')}});await h.call('identity');
 const result=await h.call('startEvents');assert.equal(result.ok,false);assert(!JSON.stringify(result).includes('secret'));
});
test('lost session removes event listener instead of forwarding further messages',async()=>{
 const h=harness([identityResponse()]);await h.call('identity');await h.call('startEvents');h.posts.length=0;h.context.document.cookie='';
 h.socket.emit([{type:'chat.group_message.message.new',message:{id:9,lobbyId:42}}]);
 assert.equal(h.posts.length,0);assert.equal(h.socket.handlers.size,0);
});
test('English UI translates generated placeholders without altering message bodies', async () => {
  const h=harness([{data:{messages:[{id:1,content_state:{unknown:true}},{id:2,media:{id:3}},{id:3,is_erased:true},{id:4,plaintext:'[첨부/미디어 메시지]'}]}}]);
  const r=await h.context.window.SpectrumProbe('history',{lobbyId:2},'en');
  assert.equal(r.data.messages[0].text,'[Unsupported message format]');
  assert.equal(r.data.messages[1].text,'[Attachment / media message]');
  assert.equal(r.data.messages[2].text,'[Deleted message]');
  assert.equal(r.data.messages[3].text,'[첨부/미디어 메시지]');
});
