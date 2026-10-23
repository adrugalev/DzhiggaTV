import {afterEach, beforeEach, describe, expect, it, vi} from 'vitest';
import {StrictMode} from 'react';
import {act, cleanup, fireEvent, render, screen, waitFor} from '@testing-library/react';
import {Television} from './main';
import {api, setSession} from './api';
import {progress, remember, readRecent} from './types';
import type {Session, PlayerState} from './types';

const session:Session={name:'iPhone',csrf:'csrf-test',serverTime:new Date().toISOString(),policy:{viewing:true,control:true,volume:true,multiscreen:true,favorites:true,directUrls:false,transcoding:false,maximumStreams:2}};
const now=Date.now(), programme={title:'Новости',start:new Date(now-600000).toISOString(),end:new Date(now+600000).toISOString()};
const channel={id:'one',name:'Тест канал',category:'Новости',favorite:false,number:1,current:programme};
const catalog={channels:[channel],total:1,categories:['Новости'],playlists:[],collections:[],revision:1};
let requests:{path:string;body?:Record<string,unknown>}[]=[];
let sockets:{onmessage:((event:{data:string})=>void)|null}[]=[];
const pcState:PlayerState={channelId:'one',channelName:channel.name,status:'Эфир',volume:70,muted:false,paused:false,fullscreen:false,audio:[],multiscreen:{enabled:false,fullscreen:false,channels:[]},revision:1};
beforeEach(()=>{
  requests=[];sockets=[];localStorage.clear();setSession(session);
  vi.stubGlobal('ResizeObserver',class {observe(){}disconnect(){}});
  vi.stubGlobal('WebSocket',class {onopen:unknown;onmessage:((event:{data:string})=>void)|null=null;onclose:unknown;onerror:unknown;constructor(){sockets.push(this);}close(){}});
  vi.spyOn(HTMLMediaElement.prototype,'play').mockResolvedValue(undefined);
  vi.spyOn(HTMLMediaElement.prototype,'pause').mockImplementation(()=>{});
  vi.spyOn(HTMLMediaElement.prototype,'load').mockImplementation(()=>{});
  vi.stubGlobal('fetch',vi.fn(async (path:string,init?:RequestInit)=>{
    requests.push({path,body:init?.body?JSON.parse(String(init.body)):undefined});
    const body=init?.body?JSON.parse(String(init.body)):undefined;
    const mode=body?.transcode?'transcode':body?.compatible?'compatible':'proxy';
    const data=path.startsWith('/api/catalog')?catalog:path==='/api/stream'?{url:mode==='proxy'?'/media/stream/root':'/converted/stream/index.m3u8',mode}:path==='/api/session'?session:{};
    return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
  }));
});
afterEach(()=>{cleanup();vi.restoreAllMocks();vi.unstubAllGlobals();});

function initialPc(snapshot:PlayerState=pcState, remoteChannel:typeof channel|null=channel) {
  const fallback=vi.mocked(fetch).getMockImplementation()!;
  vi.mocked(fetch).mockImplementation(async(input,init)=>{
    const path=String(input);
    if(path==='/api/state'||path.startsWith('/api/catalog?ids=')){
      requests.push({path});return new Response(JSON.stringify(path==='/api/state'?snapshot:{...catalog,channels:remoteChannel?[remoteChannel]:[]}));
    }
    return fallback(input,init);
  });
}

describe('initial PC channel',()=>{
  it('starts the current PC channel beyond the first catalog page instead of mobile history',async()=>{
    remember('one');const distant={...channel,id:'pc-999',name:'Канал на компьютере'};
    initialPc({...pcState,channelId:distant.id,channelName:distant.name},distant);
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    await waitFor(()=>expect(result.container.querySelector('video')?.getAttribute('src')).toBe('/media/stream/root'));
    expect(requests.some(r=>r.path==='/api/catalog?ids=pc-999')).toBe(true);
    expect(requests.filter(r=>r.path==='/api/stream').map(r=>r.body?.channelId)).toEqual(['pc-999']);
    expect(requests.filter(r=>r.path==='/api/command')).toHaveLength(0);
    expect(screen.getByRole('heading',{name:distant.name})).toBeTruthy();
  });
  it('starts only once and keeps mobile viewing independent of subsequent PC changes',async()=>{
    initialPc();render(<Television session={session} onLogout={()=>{}}/>);
    await waitFor(()=>expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1));
    act(()=>sockets[0].onmessage?.({data:JSON.stringify({...pcState,channelId:'different',channelName:'Другой канал ПК',revision:2})}));
    fireEvent.click(screen.getByRole('button',{name:'Пульт'}));
    await screen.findByRole('heading',{name:'Другой канал ПК'});
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1);
    expect(requests.filter(r=>r.path==='/api/state')).toHaveLength(1);
  });
  it('starts just one stream through the StrictMode mount cycle',async()=>{
    initialPc();const result=render(<StrictMode><Television session={session} onLogout={()=>{}}/></StrictMode>);
    await waitFor(()=>expect(result.container.querySelector('video')?.getAttribute('src')).toBe('/media/stream/root'));
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1);
  });
  it('does not start a PC channel missing from the permitted catalog',async()=>{
    initialPc({...pcState,channelId:'hidden'},null);
    render(<Television session={session} onLogout={()=>{}}/>);
    await waitFor(()=>expect(requests.some(r=>r.path==='/api/catalog?ids=hidden')).toBe(true));
    await act(async()=>{await Promise.resolve();});
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(0);
    expect(screen.queryByRole('heading',{name:channel.name})).toBeNull();
  });
  it('does not automatically start when PC has no channel or viewing is disabled',async()=>{
    initialPc({...pcState,channelId:undefined});
    const first=render(<Television session={session} onLogout={()=>{}}/>);
    await screen.findByText(channel.name);await act(async()=>{await Promise.resolve();});
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(0);first.unmount();requests=[];
    render(<Television session={{...session,policy:{...session.policy,viewing:false}}} onLogout={()=>{}}/>);
    await screen.findByText(channel.name);
    expect(requests.filter(r=>r.path==='/api/state'||r.path==='/api/stream')).toHaveLength(0);
  });
  it('allows initial viewing without permission to control the PC',async()=>{
    initialPc();render(<Television session={{...session,policy:{...session.policy,control:false}}} onLogout={()=>{}}/>);
    await waitFor(()=>expect(requests.some(r=>r.path==='/api/stream')).toBe(true));
    expect(requests.filter(r=>r.path==='/api/command')).toHaveLength(0);
  });
  it('does not replace a manual channel choice when the initial PC request arrives late',async()=>{
    const fallback=vi.mocked(fetch).getMockImplementation()!;let finish!:(value:Response)=>void;
    vi.mocked(fetch).mockImplementation((input,init)=>String(input)==='/api/state'?new Promise(resolve=>{finish=resolve;}):fallback(input,init));
    render(<Television session={session} onLogout={()=>{}}/>);
    fireEvent.click(await screen.findByText(channel.name));
    await waitFor(()=>expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1));
    await act(async()=>{finish(new Response(JSON.stringify({...pcState,channelId:'late'})));});
    expect(requests.filter(r=>r.path==='/api/stream').map(r=>r.body?.channelId)).toEqual(['one']);
    expect(requests.filter(r=>r.path.startsWith('/api/catalog?ids='))).toHaveLength(0);
  });
  it('falls back to muted autoplay and enables sound in one tap on the same stream',async()=>{
    initialPc();let tapped=false;
    vi.mocked(HTMLMediaElement.prototype.play).mockImplementation(function(this:HTMLMediaElement){return this.muted||tapped?Promise.resolve():Promise.reject(new DOMException('Gesture required','NotAllowedError'));});
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    const sound=await screen.findByRole('button',{name:'Включить звук'}), video=result.container.querySelector('video')!;
    expect(video.muted).toBe(true);tapped=true;fireEvent.click(sound);
    await waitFor(()=>expect(screen.queryByRole('button',{name:'Включить звук'})).toBeNull());
    expect(video.muted).toBe(false);expect(result.container.querySelector('video')).toBe(video);
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1);
  });
  it('offers a clear start action when the browser also blocks muted autoplay',async()=>{
    initialPc();let tapped=false;
    vi.mocked(HTMLMediaElement.prototype.play).mockImplementation(()=>tapped?Promise.resolve():Promise.reject(new DOMException('Blocked','NotAllowedError')));
    render(<Television session={session} onLogout={()=>{}}/>);
    const start=await screen.findByRole('button',{name:'Начать просмотр'});tapped=true;fireEvent.click(start);
    await waitFor(()=>expect(screen.queryByRole('button',{name:'Начать просмотр'})).toBeNull());
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1);
    expect(requests.filter(r=>r.path==='/api/command')).toHaveLength(0);
  });
});
describe('independent mobile playback',()=>{
  it('starts the mounted media element inside the channel tap before awaiting the server',async()=>{
    let inTap=false;
    const allowed=new WeakSet<HTMLMediaElement>(), plays:{source:string|null;gesture:boolean}[]=[];
    vi.mocked(HTMLMediaElement.prototype.play).mockImplementation(function(this:HTMLMediaElement){
      plays.push({source:this.getAttribute('src'),gesture:inTap});
      if(inTap)allowed.add(this);
      return allowed.has(this)?Promise.resolve():Promise.reject(new DOMException('Gesture required','NotAllowedError'));
    });
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    const element=result.container.querySelector('video')!;
    expect(plays).toHaveLength(0);
    const channelButton=await screen.findByText('Тест канал');
    inTap=true;fireEvent.click(channelButton);inTap=false;
    expect(plays[0]).toEqual({source:'/playback-primer.mp4',gesture:true});
    await waitFor(()=>expect(element.getAttribute('src')).toBe('/media/stream/root'));
    expect(result.container.querySelector('video')).toBe(element);
    expect(plays).toEqual([{source:'/playback-primer.mp4',gesture:true},{source:'/media/stream/root',gesture:false}]);
    expect(element.muted).toBe(false);
    fireEvent.pause(element);fireEvent.canPlay(element);
    expect(plays).toHaveLength(2);
    expect(screen.queryByText(/Нажмите ▶/)).toBeNull();
  });
  it('cancels a pending sound start when viewing is stopped before priming finishes',async()=>{
    let finish!:()=>void;
    vi.mocked(HTMLMediaElement.prototype.play).mockImplementationOnce(()=>new Promise<void>(resolve=>{finish=resolve;}));
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    const element=result.container.querySelector('video')!;
    fireEvent.click(await screen.findByText('Тест канал'));
    await waitFor(()=>expect(requests.some(r=>r.path==='/api/stream')).toBe(true));
    await act(async()=>{await Promise.resolve();await Promise.resolve();});
    fireEvent.click(screen.getByRole('button',{name:'Остановить просмотр'}));
    await act(async()=>{finish();await Promise.resolve();});
    expect(element.getAttribute('src')).toBe('/playback-primer.mp4');
    expect(HTMLMediaElement.prototype.play).toHaveBeenCalledTimes(1);
    expect(requests.filter(r=>r.path==='/api/stream/stop')).toHaveLength(1);
    expect(screen.queryByRole('button',{name:'Остановить просмотр'})).toBeNull();
  });
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
  it('recovers a codec error once with copied video and AAC, then offers full conversion',async()=>{
    const result=render(<Television session={{...session,policy:{...session.policy,transcoding:true}}} onLogout={()=>{}}/>);
    fireEvent.click(await screen.findByText('Тест канал'));
    const video=result.container.querySelector('video')!;
    await waitFor(()=>expect(video.getAttribute('src')).toBe('/media/stream/root'));
    Object.defineProperty(video,'error',{configurable:true,value:{code:4}});fireEvent.error(video);
    await waitFor(()=>expect(video.getAttribute('src')).toBe('/converted/stream/index.m3u8'));
    expect(requests.filter(r=>r.path==='/api/stream'&&r.body?.compatible===true)).toHaveLength(1);
    expect(requests.filter(r=>r.path==='/api/stream'&&r.body?.transcode===true)).toHaveLength(0);
    fireEvent.error(video);
    expect(await screen.findByText(/Этот формат видео тоже несовместим/)).toBeTruthy();
    expect(requests.filter(r=>r.path==='/api/stream'&&r.body?.compatible===true)).toHaveLength(1);
    fireEvent.click(screen.getByRole('button',{name:'Преобразовать видео в H.264'}));
    await waitFor(()=>expect(requests.some(r=>r.path==='/api/stream'&&r.body?.transcode===true)).toBe(true));
    expect(requests.filter(r=>r.path==='/api/command')).toHaveLength(0);
  });
  it('does not start an encoder after a network error',async()=>{
    const result=render(<Television session={{...session,policy:{...session.policy,transcoding:true}}} onLogout={()=>{}}/>);
    fireEvent.click(await screen.findByText('Тест канал'));
    const video=result.container.querySelector('video')!;
    await waitFor(()=>expect(video.getAttribute('src')).toBe('/media/stream/root'));
    Object.defineProperty(video,'error',{configurable:true,value:{code:2}});fireEvent.error(video);
    expect(await screen.findByText(/Safari не смог загрузить поток/)).toBeTruthy();
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1);
  });
  it('explains the desktop compatibility setting when conversion is disabled',async()=>{
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    fireEvent.click(await screen.findByText('Тест канал'));
    const video=result.container.querySelector('video')!;
    await waitFor(()=>expect(video.getAttribute('src')).toBe('/media/stream/root'));
    Object.defineProperty(video,'error',{configurable:true,value:{code:3}});fireEvent.error(video);
    expect(await screen.findByText(/включите «Совместимость iPhone»/)).toBeTruthy();
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1);
    expect(screen.queryByRole('button',{name:'Преобразовать видео в H.264'})).toBeNull();
  });
  it('virtualizes a large catalog',async()=>{
    const large={...catalog,total:3000,channels:Array.from({length:100},(_,i)=>({...channel,id:String(i),name:'Канал '+i}))};
    vi.mocked(fetch).mockImplementation(async()=>new Response(JSON.stringify(large)));
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    await screen.findByText('Канал 0');
    expect(result.container.querySelectorAll('.channel-row').length).toBeLessThan(20);
  });
  it('keeps channels searchable while browsing filters or collapsing the mounted player',async()=>{
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    fireEvent.click(await screen.findByText('Тест канал'));
    const video=result.container.querySelector('video')!;
    await waitFor(()=>expect(video.getAttribute('src')).toBe('/media/stream/root'));
    expect(screen.queryByLabelText('Категория')).toBeNull();
    expect(screen.getByLabelText('Поиск канала или передачи')).toBeTruthy();
    fireEvent.click(screen.getByRole('button',{name:'Фильтры каналов'}));
    expect(screen.getByLabelText('Категория')).toBeTruthy();
    fireEvent.click(screen.getByRole('button',{name:'Фильтры каналов'}));
    expect(screen.queryByLabelText('Категория')).toBeNull();
    fireEvent.click(screen.getByRole('button',{name:'Свернуть плеер'}));
    expect(screen.getByRole('button',{name:'Развернуть плеер'}).getAttribute('aria-expanded')).toBe('false');
    expect(result.container.querySelector('video')).toBe(video);
    expect(video.getAttribute('src')).toBe('/media/stream/root');
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(1);
    expect(requests.filter(r=>r.path==='/api/stream/stop')).toHaveLength(0);
    fireEvent.click(screen.getByRole('button',{name:'Развернуть плеер'}));
    expect(result.container.querySelector('video')).toBe(video);
  });
});
describe('programme search playback',()=>{
  const archive={title:'Архивный матч',start:new Date(now-7200000).toISOString(),end:new Date(now-3600000).toISOString()};
  function searched(match=archive){
    initialPc({...pcState,channelId:undefined});
    const fallback=vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async(input,init)=>{
      const path=String(input);
      if(path.startsWith('/api/catalog?')&&new URL(path,'http://localhost').searchParams.get('search')){
        requests.push({path});return new Response(JSON.stringify({...catalog,channels:[{...channel,match}]}));
      }
      return fallback(input,init);
    });
  }
  it('plays the selected archive on the phone and retains its time through codec retries',async()=>{
    searched();const result=render(<Television session={{...session,policy:{...session.policy,transcoding:true}}} onLogout={()=>{}}/>);
    fireEvent.change(screen.getByLabelText('Поиск канала или передачи'),{target:{value:'Архивный матч'}});
    fireEvent.click(await screen.findByText(archive.title));
    const video=result.container.querySelector('video')!;
    await waitFor(()=>expect(video.getAttribute('src')).toBe('/media/stream/root'));
    expect(requests.find(r=>r.path==='/api/stream')?.body?.programmeStart).toBe(archive.start);
    expect(screen.getByText('СМОТРЮ АРХИВ')).toBeTruthy();
    Object.defineProperty(video,'error',{configurable:true,value:{code:4}});fireEvent.error(video);
    await waitFor(()=>expect(video.getAttribute('src')).toBe('/converted/stream/index.m3u8'));
    fireEvent.error(video);fireEvent.click(await screen.findByRole('button',{name:'Преобразовать видео в H.264'}));
    await waitFor(()=>expect(requests.some(r=>r.body?.transcode===true)).toBe(true));
    expect(requests.filter(r=>r.path==='/api/stream').map(r=>r.body?.programmeStart)).toEqual([archive.start,archive.start,archive.start]);
    expect(requests.filter(r=>r.path==='/api/command')).toHaveLength(0);
  });
  it('sends the selected programme to the Windows player in PC mode',async()=>{
    searched();render(<Television session={session} onLogout={()=>{}}/>);
    fireEvent.click(screen.getByRole('button',{name:/Управлять ПК/}));
    fireEvent.change(screen.getByLabelText('Поиск канала или передачи'),{target:{value:archive.title}});
    fireEvent.click(await screen.findByText(archive.title));
    await waitFor(()=>expect(requests.some(r=>r.path==='/api/command'&&r.body?.programmeStart===archive.start)).toBe(true));
    expect(requests.filter(r=>r.path==='/api/stream')).toHaveLength(0);
  });
  it('keeps current playback when the provider refuses the selected archive',async()=>{
    searched();const fallback=vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async(input,init)=>{
      if(String(input)==='/api/stream'&&init?.body&&JSON.parse(String(init.body)).programmeStart)return new Response(JSON.stringify({error:'Провайдер вернул пустой архив'}),{status:409});
      return fallback(input,init);
    });
    const result=render(<Television session={session} onLogout={()=>{}}/>);
    fireEvent.click(await screen.findByText(channel.name));
    const video=result.container.querySelector('video')!;await waitFor(()=>expect(video.getAttribute('src')).toBe('/media/stream/root'));
    const loads=vi.mocked(HTMLMediaElement.prototype.load).mock.calls.length;
    fireEvent.change(screen.getByLabelText('Поиск канала или передачи'),{target:{value:archive.title}});
    fireEvent.click(await screen.findByText(archive.title));
    await screen.findByText('Провайдер вернул пустой архив');
    expect(video.getAttribute('src')).toBe('/media/stream/root');expect(result.container.querySelector('video')).toBe(video);
    expect(vi.mocked(HTMLMediaElement.prototype.load).mock.calls).toHaveLength(loads);
    expect(screen.queryByText('СМОТРЮ АРХИВ')).toBeNull();
  });
  it('opens live TV for a search match that is still on air',async()=>{
    searched({...programme,title:'Текущий матч'});render(<Television session={session} onLogout={()=>{}}/>);
    fireEvent.change(screen.getByLabelText('Поиск канала или передачи'),{target:{value:'Текущий матч'}});
    fireEvent.click(await screen.findByText('Текущий матч'));
    await waitFor(()=>expect(requests.some(r=>r.path==='/api/stream')).toBe(true));
    expect(requests.find(r=>r.path==='/api/stream')?.body?.programmeStart).toBeUndefined();
    expect(screen.queryByText('СМОТРЮ АРХИВ')).toBeNull();
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
