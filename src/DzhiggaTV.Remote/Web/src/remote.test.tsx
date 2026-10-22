import {afterEach, beforeEach, describe, expect, it, vi} from 'vitest';
import {cleanup, fireEvent, render, screen, waitFor} from '@testing-library/react';
import {Television} from './main';
import {api, setSession} from './api';
import {progress, remember, readRecent} from './types';
import type {Session} from './types';

const session:Session={name:'iPhone',csrf:'csrf-test',serverTime:new Date().toISOString(),policy:{viewing:true,control:true,volume:true,multiscreen:true,favorites:true,directUrls:false,transcoding:false,maximumStreams:2}};
const now=Date.now(), programme={title:'Новости',start:new Date(now-600000).toISOString(),end:new Date(now+600000).toISOString()};
const channel={id:'one',name:'Тест канал',category:'Новости',favorite:false,number:1,current:programme};
const catalog={channels:[channel],total:1,categories:['Новости'],playlists:[],collections:[],revision:1};
let requests:{path:string;body?:Record<string,unknown>}[]=[];
beforeEach(()=>{
  requests=[];localStorage.clear();setSession(session);
  vi.stubGlobal('ResizeObserver',class {observe(){}disconnect(){}});
  vi.stubGlobal('WebSocket',class {onopen:unknown;onmessage:unknown;onclose:unknown;onerror:unknown;close(){}});
  vi.spyOn(HTMLMediaElement.prototype,'play').mockResolvedValue(undefined);
  vi.spyOn(HTMLMediaElement.prototype,'pause').mockImplementation(()=>{});
  vi.spyOn(HTMLMediaElement.prototype,'load').mockImplementation(()=>{});
  vi.stubGlobal('fetch',vi.fn(async (path:string,init?:RequestInit)=>{
    requests.push({path,body:init?.body?JSON.parse(String(init.body)):undefined});
    const data=path.startsWith('/api/catalog')?catalog:path==='/api/stream'?{url:'/media/stream/root',mode:'proxy'}:path==='/api/session'?session:{};
    return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
  }));
});
afterEach(()=>{cleanup();vi.restoreAllMocks();vi.unstubAllGlobals();});
describe('independent mobile playback',()=>{
  it('starts a mobile stream without sending a PC command',async()=>{
    render(<Television session={session} onLogout={()=>{}}/>);
    fireEvent.click(await screen.findByText('Тест канал'));
    await waitFor(()=>expect(requests.some(r=>r.path==='/api/stream'&&r.body?.channelId==='one')).toBe(true));
    expect(requests.filter(r=>r.path==='/api/command')).toHaveLength(0);
    expect(readRecent()).toEqual(['one']);
  });
  it('sends a PC command when PC mode is explicitly selected',async()=>{
    render(<Television session={session} onLogout={()=>{}}/>);
    fireEvent.click(screen.getByRole('button',{name:/Управлять ПК/}));
    fireEvent.click(await screen.findByText('Тест канал'));
    await waitFor(()=>expect(requests.some(r=>r.path==='/api/command'&&r.body?.action==='play')).toBe(true));
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(0);
  });
  it('does not offer PC controls when permission is disabled',()=>{
    render(<Television session={{...session,policy:{...session.policy,control:false}}} onLogout={()=>{}}/>);
    expect((screen.getByRole('button',{name:/Управлять ПК/}) as HTMLButtonElement).disabled).toBe(true);
  });
  it('virtualizes a large catalog',async()=>{
    const large={...catalog,total:3000,channels:Array.from({length:100},(_,i)=>({...channel,id:String(i),name:'Канал '+i}))};
    vi.mocked(fetch).mockImplementation(async()=>new Response(JSON.stringify(large)));
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    await screen.findByText('Канал 0');
    expect(result.container.querySelectorAll('.channel-row').length).toBeLessThan(20);
  });
});
describe('API and local history',()=>{
  it('sends CSRF only in the command header and keeps credentials out of URLs',async()=>{
    await api('/api/command',{action:'mute'});
    const [path,init]=vi.mocked(fetch).mock.calls[0];
    expect(path).toBe('/api/command');expect((init?.headers as Record<string,string>)['X-DzhiggaTV-CSRF']).toBe('csrf-test');expect(init?.credentials).toBe('same-origin');
  });
  it('keeps a bounded independent recent list',()=>{for(let i=0;i<40;i++)remember(String(i));remember('38');expect(readRecent()).toHaveLength(20);expect(readRecent()[0]).toBe('38');expect(readRecent().filter(x=>x==='38')).toHaveLength(1);});
  it('handles malformed local history',()=>{localStorage.setItem('dzhigga.recent','not json');expect(readRecent()).toEqual([]);localStorage.setItem('dzhigga.recent','{}');expect(readRecent()).toEqual([]);});
  it('clamps EPG progress and tolerates missing schedules',()=>{expect(progress()).toBe(0);expect(progress(programme,now-900000)).toBe(0);expect(progress(programme,now)).toBe(50);expect(progress(programme,now+900000)).toBe(100);});
});
