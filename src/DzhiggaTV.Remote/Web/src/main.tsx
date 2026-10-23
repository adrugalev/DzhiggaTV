import React, {useCallback, useEffect, useRef, useState} from 'react';
import {createRoot} from 'react-dom/client';
import {Tv, Star, CalendarDays, SlidersHorizontal, Settings, Search, Play, Monitor, Smartphone, ChevronLeft, ChevronRight, ChevronUp, ChevronDown, Volume2, VolumeX, Pause, Maximize, Minimize, RotateCcw, X, Wifi, WifiOff, Grid2X2, Check, MoreHorizontal, RefreshCw, PictureInPicture2, Cast, Info, LogOut, Square} from 'lucide-react';
import {api, ApiError, setSession, stopOnExit} from './api';
import {progress, range, time, readRecent, remember} from './types';
import type {Channel, Catalog, Command, PlayerState, Programme, Session} from './types';
import './style.css';

const message = (e: unknown) => e instanceof Error ? e.message : 'Не удалось выполнить действие.';
const empty: Catalog = {channels:[], total:0, categories:[], playlists:[], collections:[], revision:0};
// A tiny local H.264/AAC clip with a silent audio track. Keep one media element:
// Safari grants sound playback to that element when play() runs in the tap handler.
const playbackPrimer='/playback-primer.mp4';
function prepareVideo(element:HTMLVideoElement) {
  element.loop=true;
  if(element.getAttribute('src')!==playbackPrimer){element.src=playbackPrimer;element.load();}
}
type Tab = 'tv'|'favorites'|'guide'|'remote'|'settings';

function App() {
  const [session, login] = useState<Session|null>(null), [starting, setStarting] = useState(true);
  const [pair, setPair] = useState<{verification:string; id:string}|null>(null), [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  useEffect(()=>{
    let alive = true; const abort = new AbortController(); let timer: ReturnType<typeof setTimeout>;
    const code = new URLSearchParams(location.hash.slice(1)).get('pair');
    if (code) history.replaceState(null,'',location.pathname);
    const connect = async () => {
      try {const s = await api<Session>('/api/session',undefined,abort.signal); if(alive){setSession(s);login(s);} }
      catch(e) {
        if(!alive) return;
        if(code && e instanceof ApiError && e.status===401) {
          try {
            const p = await api<{id:string;verification:string}>('/api/pair',{code,name:/iPad/.test(navigator.userAgent)?'iPad':/iPhone/.test(navigator.userAgent)?'iPhone':'Браузер'},abort.signal);
            if(!alive)return; setPair(p);
            const poll = async () => {
              try {const status = await api<{status:string}>('/api/pair/status?id='+encodeURIComponent(p.id),undefined,abort.signal);
                if(!alive)return;
                if(status.status==='approved'){const s=await api<Session>('/api/session',undefined,abort.signal);if(alive){setSession(s);login(s);setPair(null);}}
                else if(status.status==='pending')timer=setTimeout(poll,1000);
                else {setPair(null);setError(status.status==='denied'?'Подключение отклонено на компьютере.':'Код истёк. Обновите QR-код на компьютере.');}
              }catch(ex){if(alive){setPair(null);setError(message(ex));}}
            }; timer=setTimeout(poll,800);
          } catch(ex){if(alive)setError(message(ex));}
        } else setError(e instanceof ApiError && e.status===401?'Откройте настройки Remote на компьютере и отсканируйте QR-код.':message(e));
      }finally{if(alive)setStarting(false);}
    }; void connect();
    return()=>{alive=false;abort.abort();clearTimeout(timer);};
  },[retry]);
  if(session)return <Television session={session} onLogout={()=>{login(null);setError('Доступ устройства отозван. Для подключения нужен новый QR-код.');}} />;
  return <main className="welcome"><div className="brand-mark"><Tv size={38}/></div><span className="eyebrow">ВАШ ТЕЛЕВИЗОР. РЯДОМ.</span><h1>Dzhigga<span>TV</span><small>Remote</small></h1>
    {pair ? <section className="panel pairing"><span className="loading-dot"/><h2>Подтвердите на компьютере</h2><p>Код на обоих экранах должен совпадать.</p><strong className="verification">{pair.verification}</strong><p>После подтверждения здесь откроются каналы.</p></section>
    : <section className="panel"><h2>{starting?'Подключаемся…':'Подключите телефон'}</h2><p>{error||'Откройте камеру iPhone и отсканируйте QR-код в DzhiggaTV.'}</p>{!starting&&<button className="primary" onClick={()=>{setError('');setStarting(true);setRetry(n=>n+1);}}><RefreshCw size={18}/> Проверить подключение</button>}</section>}
    <p className="fine">Телефон и компьютер должны быть в одной домашней сети. Проверьте, что Remote включён, компьютер не спит, а гостевой Wi-Fi или VPN не блокирует доступ.</p></main>;
}

export function Television({session,onLogout}:{session:Session;onLogout:()=>void}) {
  const [tab,setTab] = useState<Tab>('tv'), [mode,setMode] = useState<'watch'|'pc'>('watch');
  const [online,setOnline] = useState(true), [state,setState] = useState<PlayerState>();
  const [toast,setToast] = useState(''), [busy,setBusy] = useState(false);
  const [search,setSearch] = useState(''), [debounced,setDebounced] = useState('');
  const [category,setCategory] = useState(''), [playlist,setPlaylist] = useState(''), [collection,setCollection] = useState(''), [sort,setSort] = useState('playlist');
  const [catalog,setCatalog] = useState<Catalog>(empty), [loading,setLoading] = useState(false), [catalogError,setCatalogError] = useState(''), [refresh,setRefresh] = useState(0);
  const [watching,setWatching] = useState<Channel>(), [videoUrl,setVideoUrl] = useState(''), [videoMode,setVideoMode] = useState(''), [videoError,setVideoError] = useState(''), [videoLoading,setVideoLoading] = useState(false);
  const [playbackAction,setPlaybackAction] = useState<'sound'|'play'|null>(null);
  const initialRequest=useRef<AbortController|null>(null), initialComplete=useRef(false);
  const [detail,setDetail] = useState<{channel:Channel;programme?:Programme}>(), [context,setContext] = useState<Channel>();
  const [guideId,setGuideId] = useState(''), [guideDate,setGuideDate] = useState(''), [guide,setGuide] = useState<Programme[]>([]), [guideLoading,setGuideLoading] = useState(false);
  const [pickSlot,setPickSlot] = useState<number|null>(null), [recent,setRecent] = useState(readRecent);
  const [permission,setPermission] = useState(session.policy);
  const [filtersOpen,setFiltersOpen] = useState(false), [playerCollapsed,setPlayerCollapsed] = useState(false), [searchFocused,setSearchFocused] = useState(false);
  const video = useRef<HTMLVideoElement>(null), activeStream = useRef(false), switchGeneration=useRef(0), switching=useRef<AbortController|null>(null), catalogAbort=useRef<AbortController|null>(null), loadingRef=useRef(false), compatibilityAttempted=useRef(false);
  useEffect(()=>{if(video.current)prepareVideo(video.current);},[]);
  const toastTimer = useRef<ReturnType<typeof setTimeout>|undefined>(undefined);
  const notify = useCallback((text:string)=>{setToast(text);clearTimeout(toastTimer.current);toastTimer.current=setTimeout(()=>setToast(''),5000);},[]);
  useEffect(()=>{const t=setTimeout(()=>setDebounced(search.trim()),250);return()=>clearTimeout(t);},[search]);
  useEffect(()=>{
    let gone=false, socket:WebSocket|undefined, timer:ReturnType<typeof setTimeout>, heartbeat:ReturnType<typeof setInterval>, delay=1000;
    const connect=()=>{
      if(gone)return;
      socket=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/events`);
      socket.onopen=()=>{if(gone)return;setOnline(true);delay=1000;socket?.send('ping');clearInterval(heartbeat);heartbeat=setInterval(()=>{if(socket?.readyState===WebSocket.OPEN)socket.send('ping');},10000);void api<Session>('/api/session').then(s=>{setSession(s);setPermission(s.policy);}).catch(e=>{if(e instanceof ApiError&&e.status===401)onLogout();});};
      socket.onmessage=e=>{if(gone)return;try{setState(JSON.parse(e.data));}catch{}};
      socket.onclose=()=>{clearInterval(heartbeat);if(gone)return;setOnline(false);timer=setTimeout(()=>{void api<Session>('/api/session').then(()=>connect()).catch(e=>{if(e instanceof ApiError&&e.status===401)onLogout();else {timer=setTimeout(connect,delay);delay=Math.min(15000,delay*2);}});},delay);delay=Math.min(15000,delay*2);};
      socket.onerror=()=>socket?.close();
    };connect(); return()=>{gone=true;clearTimeout(timer);clearInterval(heartbeat);socket?.close();};
  },[onLogout]);
  const revision=state?.revision??0;
  useEffect(()=>{
    if(!watching)return;let alive=true;
    const update=()=>{void api<Catalog>('/api/catalog?ids='+encodeURIComponent(watching.id)).then(c=>{if(alive&&c.channels[0])setWatching(previous=>({...c.channels[0],match:previous?.match}));}).catch(()=>{});};
    const timer=setInterval(update,30000);return()=>{alive=false;clearInterval(timer);};
  },[watching?.id]);
  const queryKey=JSON.stringify([debounced,category,playlist,collection,sort,tab==='favorites',pickSlot!==null,refresh,revision,recent]);
  const loadCatalog=useCallback(async (append=false)=>{
    if(loadingRef.current&&append)return;
    if(!append)catalogAbort.current?.abort();
    const abort=new AbortController();catalogAbort.current=abort;loadingRef.current=true;setLoading(true);setCatalogError('');
    const offset=append?catalog.channels.length:0;
    const params=new URLSearchParams({search:debounced,category,playlist,sort,offset:String(offset),collection:tab==='favorites'?'favorites':collection==='recent'?'':collection});
    if(collection==='recent'&&tab!=='favorites')params.set('ids',recent.length?recent.join(','):'none');
    try{const result=await api<Catalog>('/api/catalog?'+params,undefined,abort.signal);if(!abort.signal.aborted)setCatalog({...result,channels:append?[...catalog.channels,...result.channels]:result.channels});}
    catch(e){if(!abort.signal.aborted){setCatalogError(message(e));if(e instanceof ApiError&&e.status===401)onLogout();}}
    finally{if(!abort.signal.aborted){loadingRef.current=false;setLoading(false);}}
  },[queryKey,catalog.channels]);
  useEffect(()=>{void loadCatalog(false);return()=>catalogAbort.current?.abort();},[queryKey]);
  useEffect(()=>{const timer=setInterval(()=>{if(document.visibilityState==='visible')setRefresh(n=>n+1);},60000);return()=>clearInterval(timer);},[]);
  const command=async (cmd:Command)=>{
    if(!online)throw new Error('Нет подключения к DzhiggaTV. Команда не отправлена.');
    const next=await api<PlayerState>('/api/command',cmd);setState(next);return next;
  };
  const act=async (cmd:Command)=>{if(busy)return;setBusy(true);try{await command(cmd);}catch(e){notify(message(e));}finally{setBusy(false);}};
  const stop=useCallback(()=>{
    initialComplete.current=true;initialRequest.current?.abort();setPlaybackAction(null);
    ++switchGeneration.current;switching.current?.abort();setVideoUrl('');setVideoError('');setVideoLoading(false);setWatching(undefined);setPlayerCollapsed(false);
    if(video.current){video.current.pause();prepareVideo(video.current);}
    if(activeStream.current){stopOnExit();activeStream.current=false;}
  },[]);
  useEffect(()=>{const exit=()=>stopOnExit();window.addEventListener('pagehide',exit);const ping=setInterval(()=>{if(activeStream.current)void api('/api/stream/ping',{}).catch(()=>{});},15000);return()=>{clearInterval(ping);window.removeEventListener('pagehide',exit);switching.current?.abort();if(activeStream.current)stopOnExit();};},[]);
  const watch=async (channel:Channel,transcode=false,compatible=false,fromGesture=true,initial=false)=>{
    if(!permission.viewing){notify('Просмотр на телефоне отключён на компьютере.');return;}
    if(fromGesture){initialComplete.current=true;initialRequest.current?.abort();setPlaybackAction(null);}
    const generation=++switchGeneration.current;switching.current?.abort();const abort=new AbortController();switching.current=abort;
    if(!transcode&&!compatible){compatibilityAttempted.current=false;setFiltersOpen(false);}
    const archive=channel.match&&Date.parse(channel.match.end)<=Date.now()?channel.match:undefined;
    const preserve=!!archive&&activeStream.current&&!!videoUrl;
    setMode('watch');setTab('tv');if(!preserve)setWatching({...channel,match:archive});setVideoError('');setVideoLoading(true);if(!preserve){setVideoUrl('');setVideoMode('');}
    // Start sound playback before the first await, while this is still the user's tap.
    // Wait until that play request settles before replacing its source. React never
    // owns src or remounts the element while switching channels or retrying codecs.
    const element=video.current;
    let priming:Promise<void>=Promise.resolve();
    if(fromGesture&&element){element.muted=false;if(!preserve)prepareVideo(element);priming=element.play().catch(()=>{});}
    try{
      const result=await api<{url:string;mode:string}>('/api/stream',{channelId:channel.id,transcode,compatible,programmeStart:archive?.start},abort.signal);
      if(generation!==switchGeneration.current||abort.signal.aborted||element!==video.current)return;
      activeStream.current=true;
      await priming;
      if(generation!==switchGeneration.current||abort.signal.aborted||element!==video.current)return;
      setWatching({...channel,match:archive});setVideoMode(result.mode);setVideoUrl(result.url);setRecent(remember(channel.id));
      if(element){element.loop=false;element.src=result.url;element.load();void element.play().catch(async e=>{
        if(generation!==switchGeneration.current||element.getAttribute('src')!==result.url||e?.name!=='NotAllowedError')return;
        setVideoLoading(false);
        if(initial&&!element.muted){
          element.muted=true;setPlaybackAction('sound');
          try{await element.play();}catch{if(generation===switchGeneration.current)setPlaybackAction('play');}
        }else{setPlaybackAction('play');notify('Нажмите ▶ на видео, чтобы начать просмотр со звуком.');}
      });}
    }catch(e){if(generation===switchGeneration.current&&!abort.signal.aborted){if(element?.getAttribute('src')===playbackPrimer)element.pause();setVideoLoading(false);if(preserve&&e instanceof ApiError&&e.status===409){setVideoError('');notify(message(e));}else setVideoError(message(e));}}
  };
  useEffect(()=>{
    if(!permission.viewing||initialComplete.current||switchGeneration.current!==0)return;
    const abort=new AbortController();initialRequest.current=abort;
    const untouched=()=>!abort.signal.aborted&&switchGeneration.current===0;
    void(async()=>{
      const snapshot=await api<PlayerState>('/api/state',undefined,abort.signal);
      if(!untouched())return;
      setState(current=>current??snapshot);
      if(!snapshot.channelId){initialComplete.current=true;return;}
      // Resolve by ID independently of paging, filters and the phone's recent history.
      const result=await api<Catalog>('/api/catalog?ids='+encodeURIComponent(snapshot.channelId),undefined,abort.signal);
      if(!untouched())return;
      initialComplete.current=true;
      const channel=result.channels.find(c=>c.id===snapshot.channelId);
      if(channel)void watch(channel,false,false,false,true);
    })().catch(e=>{if(untouched()){initialComplete.current=true;if(e instanceof ApiError&&e.status===401)onLogout();else notify('Не удалось открыть канал с компьютера. Выберите канал в списке.');}});
    return()=>{abort.abort();if(initialRequest.current===abort)initialRequest.current=null;};
  },[permission.viewing]);
  const continuePlayback=()=>{
    const element=video.current;if(!element||!videoUrl)return;
    const generation=switchGeneration.current;element.muted=false;
    void element.play().then(()=>{if(generation===switchGeneration.current)setPlaybackAction(null);}).catch(()=>{if(generation===switchGeneration.current)setPlaybackAction('play');});
  };
  const videoFailed=()=>{
    const element=video.current;
    if(!videoUrl||!watching||!element?.getAttribute('src')||element.getAttribute('src')!==videoUrl)return;
    setVideoLoading(false);
    const codecError=element.error?.code===3||element.error?.code===4;
    if(codecError&&videoMode==='proxy'&&permission.transcoding&&!compatibilityAttempted.current){
      compatibilityAttempted.current=true;notify('Подбираем совместимый звук · качество видео сохраняется');void watch(watching,false,true,false);return;
    }
    setVideoError(codecError?(videoMode==='compatible'?'Этот формат видео тоже несовместим с Safari. Можно преобразовать видео в H.264.':permission.transcoding?'Safari не поддерживает формат этого потока. Попробуйте совместимый звук или преобразование видео.':'Для этого канала включите «Совместимость iPhone» в окне Remote на компьютере. Это позволит преобразовать звук, сохранив качество видео.'):'Safari не смог загрузить поток. Проверьте сеть и лимит подключений провайдера.');
  };
  const choose=async (channel:Channel)=>{
    if(pickSlot!==null){const slots=[...(state?.multiscreen.channels??[null,null,null,null])];while(slots.length<=pickSlot)slots.push(null);slots.forEach((id,i)=>{if(id===channel.id)slots[i]=null;});slots[pickSlot]=channel.id;setPickSlot(null);setTab('remote');await act({action:'multiscreen.layout',channels:slots});return;}
    if(mode==='pc'){await act({action:'play',channelId:channel.id,programmeStart:channel.match?.start});notify(`На ПК: ${channel.match?.title??channel.name}`);}else await watch(channel);
  };
  const favorite=async (c:Channel)=>{try{await command({action:'favorite',channelId:c.id,value:c.favorite?0:1});setRefresh(n=>n+1);}catch(e){notify(message(e));}};
  useEffect(()=>{
    if(tab!=='guide'||!guideId)return;const abort=new AbortController();setGuideLoading(true);
    const start=guideDate?new Date(guideDate+'T00:00:00'):new Date(Date.now()-3600000);
    void api<Programme[]>(`/api/guide/${encodeURIComponent(guideId)}?from=${encodeURIComponent(start.toISOString())}`,undefined,abort.signal).then(setGuide).catch(e=>{if(!abort.signal.aborted)notify(message(e));}).finally(()=>{if(!abort.signal.aborted)setGuideLoading(false);});
    return()=>abort.abort();
  },[tab,guideId,guideDate,revision]);
  const navigate=(next:Tab)=>{initialComplete.current=true;initialRequest.current?.abort();setPickSlot(null);setTab(next);if(next==='guide'&&!guideId)setGuideId(watching?.id??state?.channelId??catalog.channels[0]?.id??'');};
  const currentGuide=catalog.channels.find(c=>c.id===guideId)??(watching?.id===guideId?watching:undefined)??(state?.channelId===guideId?{id:guideId,name:state.channelName,category:'',favorite:false,number:0}:undefined);
  const fullscreen=()=>{const v=video.current as HTMLVideoElement&{webkitEnterFullscreen?:()=>void};if(!v)return;if(document.fullscreenElement)void document.exitFullscreen();else if(v.webkitEnterFullscreen)v.webkitEnterFullscreen();else void v.requestFullscreen?.();};
  const setRemoteMode=(pc:boolean)=>{if(pc){stop();setMode('pc');}else setMode('watch');};
  const showPlayer=!!watching&&tab!=='remote';
  const watchingProgramme=watching?.match??watching?.current;
  const catalogWatching=!!watching&&(tab==='tv'||tab==='favorites')&&pickSlot===null;
  const expandedCatalog=!catalogWatching||filtersOpen;
  const activeFilters=[category,playlist,collection,sort!=='playlist'?sort:''].filter(Boolean).length;
  const actualVideo=()=>!!videoUrl&&video.current?.getAttribute('src')===videoUrl;
  return <div className={'app '+(showPlayer?'has-player ':'')+(catalogWatching?'catalog-view':'')}>
    <header><div className="wordmark">Dzhigga<span>TV</span><small>REMOTE</small></div><div className={'connection '+(online?'':'offline')}>{online?<Wifi size={14}/>:<WifiOff size={14}/>}<span>{online?'Дома':'Нет подключения'}</span></div></header>
    {!online&&<div className="offline-banner">Нет подключения к DzhiggaTV · пробуем восстановить связь</div>}
    <section hidden={!showPlayer} className={'viewer '+(tab==='settings'?'viewer-small ':'')+(playerCollapsed||searchFocused?'viewer-collapsed':'')} aria-label="Плеер телефона">
      <div className="video-frame" id="remote-video"><video ref={video} playsInline controls preload="auto" onWaiting={()=>{if(actualVideo())setVideoLoading(true);}} onPlaying={()=>{if(actualVideo()){setVideoLoading(false);setVideoError('');setPlaybackAction(action=>action===null?null:video.current?.muted?'sound':null);}}} onVolumeChange={()=>{if(!video.current?.muted)setPlaybackAction(null);}} onCanPlay={()=>{if(actualVideo())setVideoLoading(false);}} onError={videoFailed} onDoubleClick={fullscreen}/>
        {videoLoading&&<div className="video-status"><span className="loading-dot"/> Подключение…</div>}
        {playbackAction&&!videoError&&<button className="playback-action" onClick={continuePlayback}>{playbackAction==='sound'?<Volume2 size={17}/>:<Play size={17}/>} {playbackAction==='sound'?'Включить звук':'Начать просмотр'}</button>}
        <button className="icon close-video" aria-label="Остановить просмотр" onClick={stop}><X size={18}/></button>
      </div>
      {watching&&<><div className="now-playing"><div><span className="eyebrow">{watching.match?'СМОТРЮ АРХИВ':'СМОТРЮ ЗДЕСЬ'} {videoMode==='transcode'?'· H.264 / AAC':videoMode==='compatible'?'· ИСХОДНОЕ ВИДЕО / AAC':''}</span><h2>{watching.name}</h2><button className="programme-link" onClick={()=>setDetail({channel:watching,programme:watchingProgramme})}>{watchingProgramme?.title??'Телепрограмма пока недоступна'} <Info size={13}/></button></div>
        <button className="icon collapse-player" aria-label={playerCollapsed?'Развернуть плеер':'Свернуть плеер'} aria-expanded={!playerCollapsed} aria-controls="remote-video" onClick={()=>setPlayerCollapsed(value=>!value)}>{playerCollapsed?<ChevronDown size={20}/>:<ChevronUp size={20}/>}</button>
        <button className="icon" aria-label="Полный экран" onClick={fullscreen}><Maximize/></button>
        {'webkitShowPlaybackTargetPicker' in (video.current??{})&&<button className="icon" aria-label="AirPlay" onClick={()=>{(video.current as HTMLVideoElement&{webkitShowPlaybackTargetPicker:()=>void}).webkitShowPlaybackTargetPicker();}}><Cast/></button>}
        {document.pictureInPictureEnabled&&<button className="icon" aria-label="Картинка в картинке" onClick={()=>void video.current?.requestPictureInPicture?.().catch(e=>notify(message(e)))}><PictureInPicture2/></button>}
      </div>
      {videoError&&<div className="video-error"><p>{videoError}</p><button onClick={()=>void watch(watching)}><RefreshCw size={16}/> Повторить</button>{permission.transcoding&&<>{videoMode!=='compatible'&&videoMode!=='transcode'&&<button onClick={()=>void watch(watching,false,true)}>Совместимый звук · без потери видео</button>}<button onClick={()=>void watch(watching,true)}>Преобразовать видео в H.264</button></>}</div>}</>}
    </section>
    {(tab==='tv'||tab==='favorites'||pickSlot!==null)&&<section className="catalog-page">
      <div className="catalog-top">{expandedCatalog&&<div className="catalog-heading"><div className="section-title"><h1>{pickSlot!==null?`Канал для экрана ${pickSlot+1}`:tab==='favorites'?'Избранное':'Телевидение'}</h1><span>{catalog.total.toLocaleString('ru')} каналов</span></div>
        <div className="mode-switch" aria-label="Режим выбора канала"><button className={mode==='watch'?'active':''} onClick={()=>setRemoteMode(false)}><Smartphone size={16}/> Смотреть здесь</button><button className={mode==='pc'?'active':''} disabled={!permission.control} onClick={()=>setRemoteMode(true)}><Monitor size={16}/> Управлять ПК</button></div>
        </div>}
        <div className="catalog-search"><label className="search"><Search size={18}/><input value={search} onFocus={()=>setSearchFocused(true)} onBlur={()=>setSearchFocused(false)} onChange={e=>setSearch(e.target.value)} placeholder="Канал, фильм или матч" aria-label="Поиск канала или передачи"/>{search&&<button className="icon" aria-label="Очистить поиск" onClick={()=>setSearch('')}><X size={16}/></button>}</label>{catalogWatching&&<button className={'filter-toggle '+(filtersOpen||activeFilters?'accent':'')} aria-label="Фильтры каналов" aria-expanded={filtersOpen} aria-controls="catalog-filters" onClick={()=>setFiltersOpen(value=>!value)}><SlidersHorizontal size={18}/>{activeFilters>0&&<small>{activeFilters}</small>}</button>}</div>
        {expandedCatalog&&<div className="filters" id="catalog-filters"><select aria-label="Категория" value={category} onChange={e=>setCategory(e.target.value)}><option value="">Все категории</option>{catalog.categories.map(c=><option key={c}>{c}</option>)}</select>
          <select aria-label="Плейлист" value={playlist} onChange={e=>setPlaylist(e.target.value)}><option value="">Все плейлисты</option>{catalog.playlists.map(c=><option key={c.id} value={c.id}>{c.name}</option>)}</select>
          <select aria-label="Подборка" value={tab==='favorites'?'favorites':collection} disabled={tab==='favorites'} onChange={e=>setCollection(e.target.value)}><option value="">Все каналы</option><option value="recent">Недавно смотрел</option>{catalog.collections.map(c=><option key={c}>{c}</option>)}</select>
          <select aria-label="Сортировка" value={sort} onChange={e=>setSort(e.target.value)}><option value="playlist">По порядку</option><option value="name">А–Я</option></select></div>}
      </div>
      {catalogError&&<div className="empty"><p>{catalogError}</p><button onClick={()=>void loadCatalog(false)}>Повторить</button></div>}
      <ChannelList compact={catalogWatching} channels={catalog.channels} resetKey={queryKey} total={catalog.total} loading={loading} loadMore={()=>void loadCatalog(true)} selected={mode==='pc'?state?.channelId:watching?.id} choose={c=>void choose(c)} favorite={c=>void favorite(c)} canFavorite={permission.favorites} context={setContext}/>
    </section>}
    {tab==='guide'&&<section className="page guide"><div className="section-title"><h1>Телепрограмма</h1><CalendarDays size={22}/></div><p className="muted">Выберите канал в разделе ТВ или найдите его ниже.</p>
      <label className="search"><Search size={18}/><input aria-label="Найти канал для телепрограммы" placeholder="Найти канал" value={search} onChange={e=>setSearch(e.target.value)}/></label>
      <div className="guide-filters"><select aria-label="Канал телепрограммы" value={guideId} onChange={e=>setGuideId(e.target.value)}><option value="">Выберите канал</option>{currentGuide&&!catalog.channels.some(c=>c.id===guideId)&&<option value={guideId}>{currentGuide.name}</option>}{catalog.channels.map(c=><option key={c.id} value={c.id}>{c.name}</option>)}</select><input type="date" aria-label="Дата программы" value={guideDate} onChange={e=>setGuideDate(e.target.value)}/><button onClick={()=>setGuideDate('')}>Сейчас</button></div>
      {guideLoading?<div className="empty">Загружаем программу…</div>:guide.length?guide.map((p,i)=>{const live=Date.parse(p.start)<=Date.now()&&Date.parse(p.end)>Date.now();return <button className={'guide-row '+(live?'live':'')} key={p.start+i} onClick={()=>{const channel=currentGuide??{id:guideId,name:'Канал',category:'',favorite:false,number:0};setDetail({channel,programme:p});}}><time>{time(p.start)}<small>{time(p.end)}</small></time><div>{live&&<span className="eyebrow">СЕЙЧАС В ЭФИРЕ</span>}<h3>{p.title}</h3><p>{p.description}</p></div><ChevronRight size={18}/></button>;}):<div className="empty"><CalendarDays size={30}/><p>Для этого канала пока нет программы.<br/>Просмотр ТВ доступен без EPG.</p></div>}
    </section>}
    {tab==='remote'&&pickSlot===null&&<section className="page remote-page"><div className="section-title"><h1>Пульт</h1><span><Monitor size={16}/> Windows</span></div><div className="panel pc-now"><span className="eyebrow">СЕЙЧАС НА КОМПЬЮТЕРЕ</span><h2>{state?.channelName??'Подключаемся…'}</h2><p>{state?.programme?.title??state?.status}</p>{state?.programme&&<><span className="muted">{range(state.programme)}</span><div className="progress"><i style={{width:progress(state.programme)+'%'}}/></div></>}</div>
      {!permission.control&&<p className="notice">Управление плеером отключено в настройках на компьютере.</p>}
      <div className="transport"><button aria-label="Предыдущий канал" disabled={!online||busy||!permission.control} onClick={()=>void act({action:'previous'})}><ChevronLeft size={28}/><small>Канал</small></button><button className="play-button" aria-label={state?.paused?'Воспроизвести':'Пауза'} disabled={!online||busy||!permission.control} onClick={()=>void act({action:'pause'})}>{state?.paused?<Play size={30}/>:<Pause size={30}/>}</button><button aria-label="Следующий канал" disabled={!online||busy||!permission.control} onClick={()=>void act({action:'next'})}><ChevronRight size={28}/><small>Канал</small></button></div>
      <div className="remote-volume"><button className="icon" aria-label="Выключить или включить звук" disabled={!permission.volume||!permission.control} onClick={()=>void act({action:'mute'})}>{state?.muted?<VolumeX/>:<Volume2/>}</button><VolumeSlider value={state?.volume??0} disabled={!online||!permission.volume||!permission.control} commit={value=>void act({action:'volume',value})}/><b>{Math.round(state?.volume??0)}</b></div>
      <div className="quick-actions"><button disabled={!permission.control} onClick={()=>void act({action:'back'})}><RotateCcw size={20}/> Предыдущий канал</button><button disabled={!permission.control} onClick={()=>void act({action:'fullscreen',value:state?.fullscreen?0:1})}>{state?.fullscreen?<Minimize size={20}/>:<Maximize size={20}/>} {state?.fullscreen?'Выйти из полного экрана':'Полный экран'}</button><button onClick={()=>{setRemoteMode(true);navigate('tv');}}><Tv size={20}/> Выбрать канал</button></div>
      {!!state?.audio.length&&!state.multiscreen.enabled&&<label className="setting-field">Звуковая дорожка<select value={state.audio.find(a=>a.selected)?.id??''} disabled={!permission.control} onChange={e=>void act({action:'audio',value:Number(e.target.value)})}>{state.audio.map(a=><option key={a.id} value={a.id}>{a.label}</option>)}</select></label>}
      {permission.multiscreen&&permission.control&&<section className="multi"><div className="section-title"><h2>Мультиэкран</h2><button onClick={()=>void act({action:state?.multiscreen.enabled?'multiscreen.close':'multiscreen.open'})}><Grid2X2 size={18}/> {state?.multiscreen.enabled?'Выключить':'Включить'}</button></div>
        {state?.multiscreen.enabled&&<><div className="layout-picks">{[1,2,3,4].map(n=><button className={state.multiscreen.channels.length===n?'selected':''} key={n} onClick={()=>{const slots=state.multiscreen.channels.slice(0,n);while(slots.length<n)slots.push(null);void act({action:'multiscreen.layout',channels:slots});}}>{n} {n===1?'экран':'экрана'}</button>)}</div><div className="screen-grid">{state.multiscreen.channels.map((id,i)=><div className={'screen '+(id===state.multiscreen.audioId?'selected':'')} key={i}><span className="eyebrow">ЭКРАН {i+1}</span><ChannelName id={id}/><button onClick={()=>{setPickSlot(i);setSearch('');setCategory('');setPlaylist('');setCollection('');}}><Tv size={16}/> Выбрать канал</button>{id&&<div className="screen-actions"><button aria-label={`Звук экрана ${i+1}`} onClick={()=>void act({action:'multiscreen.audio',channelId:id})}><Volume2 size={17}/>{id===state.multiscreen.audioId&&<Check size={13}/>}</button><button aria-label={`Развернуть экран ${i+1}`} onClick={()=>void act({action:'multiscreen.focus',channelId:id})}><Maximize size={17}/></button></div>}</div>)}</div></>}
      </section>}
    </section>}
    {tab==='settings'&&<section className="page settings-page"><div className="section-title"><h1>Настройки</h1><Settings size={22}/></div><section className="panel"><span className="eyebrow">МОЁ УСТРОЙСТВО</span><h2>{session.name}</h2><p>{location.host}</p><div className="setting-row"><span>Связь с компьютером</span><b className={online?'accent':''}>{online?'Подключено':'Нет связи'}</b></div><div className="setting-row"><span>Мобильных потоков</span><b>до {permission.maximumStreams}</b></div><div className="setting-row"><span>Просмотр на телефоне</span><b>{permission.viewing?'Разрешён':'Выключен'}</b></div></section>
      <section className="panel"><h2>Быстрый доступ</h2><p>В Safari нажмите «Поделиться» → «На экран Домой». Так Remote будет всегда под рукой.</p><p className="fine">Локальный HTTP работает, пока компьютер включён и находится в той же сети. Офлайн-просмотр не поддерживается; HTTP не шифрует трафик.</p></section>
      <section className="panel"><h2>Звук и видео на iPhone</h2><p>Громкость телефона регулируется его кнопками. Дорожки и субтитры доступны в штатном меню видео, если они есть в потоке. AirPlay и «Картинка в картинке» появляются при поддержке браузером.</p><p className="fine">Каналы на телефоне и компьютере независимы. Лимит одновременных подключений задаёт ваш IPTV-провайдер.</p></section>
      <button className="wide" onClick={async()=>{try{await api('/api/session');notify('Компьютер отвечает. Подключение работает.');}catch(e){notify(message(e));}}}><RefreshCw size={18}/> Проверить подключение</button>
      <button className="wide danger" onClick={async()=>{stop();await api('/api/logout',{}).then(onLogout).catch(e=>notify(message(e)));}}><LogOut size={18}/> Отключить это устройство</button><p className="fine center">DzhiggaTV Remote · 2.0</p>
    </section>}
    <nav aria-label="Разделы">{([{id:'tv',title:'ТВ',icon:Tv},{id:'favorites',title:'Избранное',icon:Star},{id:'guide',title:'Программа',icon:CalendarDays},{id:'remote',title:'Пульт',icon:SlidersHorizontal},{id:'settings',title:'Настройки',icon:Settings}] as const).map(item=><button key={item.id} aria-current={tab===item.id?'page':undefined} className={tab===item.id?'active':''} onClick={()=>navigate(item.id)}><item.icon size={22}/><span>{item.title}</span></button>)}</nav>
    {toast&&<div role="status" className="toast">{toast}</div>}
    {detail&&<Modal close={()=>setDetail(undefined)}><span className="eyebrow">{detail.channel.name}</span><h2>{detail.programme?.title??detail.channel.name}</h2>{detail.programme&&<><span className="muted">{range(detail.programme)}</span><p className="tag">{detail.programme.category}</p><p className="synopsis">{detail.programme.description||'Описание этой передачи пока недоступно.'}</p></>}<p className="fine">{detail.programme&&Date.parse(detail.programme.start)>Date.now()?'Передача ещё не началась.':detail.programme&&Date.parse(detail.programme.end)<=Date.now()?'Откроется выбранная запись из архива.':'Включится текущий прямой эфир канала.'}</p><div className="modal-actions"><button className="primary" disabled={!permission.viewing||!!detail.programme&&Date.parse(detail.programme.start)>Date.now()} onClick={()=>{void watch({...detail.channel,match:detail.programme});setDetail(undefined);}}><Smartphone size={18}/> Смотреть здесь</button><button disabled={!permission.control||!!detail.programme&&Date.parse(detail.programme.start)>Date.now()} onClick={()=>{void act({action:'play',channelId:detail.channel.id,programmeStart:detail.programme?.start});setDetail(undefined);}}><Monitor size={18}/> Включить на ПК</button></div></Modal>}
    {context&&<Modal close={()=>setContext(undefined)}><span className="eyebrow">КАНАЛ</span><h2>{context.name}</h2><button className="wide primary" onClick={()=>{void watch(context);setContext(undefined);}}><Smartphone size={18}/> Смотреть на телефоне</button><button className="wide" disabled={!permission.control} onClick={()=>{void act({action:'play',channelId:context.id,programmeStart:context.match?.start});setContext(undefined);}}><Monitor size={18}/> Включить на ПК</button><button className="wide" disabled={!permission.favorites} onClick={()=>{void favorite(context);setContext(undefined);}}><Star size={18}/> {context.favorite?'Убрать из избранного':'Добавить в избранное'}</button><button className="wide" onClick={()=>{setDetail({channel:context,programme:context.match??context.current});setContext(undefined);}}><Info size={18}/> О передаче</button>{permission.favorites&&catalog.collections.map(name=><div className="collection-action" key={name}><span>{name}</span><button onClick={()=>{void act({action:'collection.add',channelId:context.id,name});setContext(undefined);}}>Добавить</button><button aria-label={`Убрать из подборки ${name}`} onClick={()=>{void act({action:'collection.remove',channelId:context.id,name});setContext(undefined);}}><X size={16}/></button></div>)}</Modal>}
  </div>;
}

function ChannelName({id}:{id:string|null}) {
  const [name,setName]=useState('Свободный экран');
  useEffect(()=>{if(!id){setName('Свободный экран');return;}const abort=new AbortController();void api<Catalog>('/api/catalog?ids='+encodeURIComponent(id)).then(c=>setName(c.channels[0]?.name??'Канал недоступен')).catch(()=>setName('Канал'));return()=>abort.abort();},[id]);
  return <h3>{name}</h3>;
}
function VolumeSlider({value,disabled,commit}:{value:number;disabled:boolean;commit:(value:number)=>void}) {
  const [local,setLocal]=useState(value), dragging=useRef(false);
  useEffect(()=>{if(!dragging.current)setLocal(value);},[value]);
  return <input aria-label="Громкость компьютера" type="range" min="0" max="100" value={local} disabled={disabled} onPointerDown={()=>{dragging.current=true;}} onChange={e=>setLocal(Number(e.target.value))} onPointerUp={e=>{dragging.current=false;commit(Number(e.currentTarget.value));}} onPointerCancel={()=>{dragging.current=false;setLocal(value);}} onKeyUp={e=>{if(['ArrowLeft','ArrowRight','Home','End'].includes(e.key))commit(Number(e.currentTarget.value));}}/>;
}
function ChannelList({compact,channels,total,loading,loadMore,resetKey,selected,choose,favorite,canFavorite,context}:{compact:boolean;channels:Channel[];total:number;loading:boolean;loadMore:()=>void;resetKey:string;selected?:string;choose:(c:Channel)=>void;favorite:(c:Channel)=>void;canFavorite:boolean;context:(c:Channel)=>void}) {
  const ref=useRef<HTMLDivElement>(null), [top,setTop]=useState(0), [height,setHeight]=useState(500);
  const rowHeight=compact?76:94, previousRowHeight=useRef(rowHeight);
  useEffect(()=>{if(ref.current&&previousRowHeight.current!==rowHeight){const next=ref.current.scrollTop/previousRowHeight.current*rowHeight;ref.current.scrollTop=next;setTop(next);previousRowHeight.current=rowHeight;}},[rowHeight]);
  useEffect(()=>{if(ref.current){ref.current.scrollTop=0;setTop(0);}},[resetKey]);
  useEffect(()=>{if(!ref.current)return;const observer=new ResizeObserver(e=>setHeight(e[0].contentRect.height));observer.observe(ref.current);return()=>observer.disconnect();},[]);
  const start=Math.max(0,Math.floor(top/rowHeight)-3), end=Math.min(channels.length,start+Math.ceil(height/rowHeight)+7);
  return <div className={'catalog-scroll '+(compact?'compact-list':'')} ref={ref} onScroll={e=>{const n=e.currentTarget;setTop(n.scrollTop);if(n.scrollTop+n.clientHeight>n.scrollHeight-400&&!loading&&channels.length<total)loadMore();}}>
    <div style={{height:start*rowHeight}}/>{channels.slice(start,end).map(c=><ChannelRow key={c.id} channel={c} selected={c.id===selected} choose={()=>choose(c)} favorite={()=>favorite(c)} canFavorite={canFavorite} context={()=>context(c)}/>)}<div style={{height:Math.max(0,(channels.length-end)*rowHeight)}}/>
    {loading&&<div className="list-loading">Загружаем каналы…</div>}{!loading&&channels.length===0&&<div className="empty"><Search size={28}/><p>Каналы не найдены.<br/>Попробуйте другой поиск или категорию.</p></div>}
    {!loading&&channels.length<total&&<button className="load-more" onClick={loadMore}>Показать ещё</button>}
  </div>;
}
function ChannelRow({channel:c,selected,choose,favorite,canFavorite,context}:{channel:Channel;selected:boolean;choose:()=>void;favorite:()=>void;canFavorite:boolean;context:()=>void}) {
  const timer=useRef<ReturnType<typeof setTimeout>|undefined>(undefined), moved=useRef({x:0,y:0}), held=useRef(false);
  const cancel=()=>clearTimeout(timer.current);useEffect(()=>cancel,[]);
  return <div className={'channel-row '+(selected?'selected':'')}>
    <button className="channel-main" onPointerDown={e=>{held.current=false;moved.current={x:e.clientX,y:e.clientY};timer.current=setTimeout(()=>{held.current=true;context();},600);}} onPointerMove={e=>{if(Math.abs(e.clientX-moved.current.x)+Math.abs(e.clientY-moved.current.y)>12)cancel();}} onPointerUp={cancel} onPointerCancel={cancel} onPointerLeave={cancel} onContextMenu={e=>{e.preventDefault();cancel();held.current=true;context();}} onClick={()=>{if(!held.current)choose();}}>
      <span className="channel-logo"><img loading="lazy" src={'/api/logo/'+encodeURIComponent(c.id)} alt="" onError={e=>{e.currentTarget.style.display='none';}}/><Tv size={22}/></span><span className="channel-copy"><strong>{c.name}</strong><span>{c.match?.title??c.current?.title??'Нет данных о передаче'}</span>{c.current?<div className="channel-bottom"><small>{range(c.match??c.current)}</small><span className="progress"><i style={{width:progress(c.current)+'%'}}/></span></div>:<small>{c.category}</small>}</span>
    </button><button className={'icon favorite '+(c.favorite?'is-favorite':'')} disabled={!canFavorite} aria-label={(c.favorite?'Убрать из избранного: ':'В избранное: ')+c.name} onClick={favorite}><Star size={20} fill={c.favorite?'currentColor':'none'}/></button><button className="icon channel-more" aria-label={'Действия с каналом '+c.name} onClick={context}><MoreHorizontal size={18}/></button>
  </div>;
}
function Modal({children,close}:{children:React.ReactNode;close:()=>void}) {
  const ref=useRef<HTMLDialogElement>(null);
  useEffect(()=>{ref.current?.showModal();return()=>ref.current?.close();},[]);
  return <dialog ref={ref} onCancel={e=>{e.preventDefault();close();}} onClick={e=>{if(e.target===e.currentTarget)close();}}><section className="modal-content"><button className="icon modal-close" aria-label="Закрыть" onClick={close}><X/></button>{children}</section></dialog>;
}

const root=document.getElementById('root');
if(root)createRoot(root).render(<App/>);
