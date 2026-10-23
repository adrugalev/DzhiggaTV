export type Programme = { title: string; start: string; end: string; description?: string; category?: string };
export type Channel = { id: string; name: string; category: string; favorite: boolean; number: number; current?: Programme; next?: Programme; match?: Programme };
export type Catalog = { channels: Channel[]; total: number; categories: string[]; playlists: {id: string; name: string}[]; collections: string[]; revision: number };
export type PlayerState = { channelId?: string; channelName: string; programme?: Programme; status: string; volume: number; muted: boolean; paused: boolean; fullscreen: boolean; audio: {id: number; label: string; selected: boolean}[]; multiscreen: {enabled: boolean; fullscreen: boolean; audioId?: string; expandedId?: string; channels: (string|null)[]}; revision: number };
export type Policy = {viewing: boolean; control: boolean; volume: boolean; multiscreen: boolean; favorites: boolean; directUrls: boolean; transcoding: boolean; maximumStreams: number};
export type Session = {name: string; csrf: string; policy: Policy; serverTime: string};
export type Command = {action: string; channelId?: string; value?: number; name?: string; channels?: (string|null)[]; programmeStart?: string};
export const time = (s: string) => new Date(s).toLocaleTimeString('ru', {hour:'2-digit', minute:'2-digit'});
export const range = (p: Programme) => `${time(p.start)} — ${time(p.end)}`;
export function progress(p?: Programme, now = Date.now()) { if (!p) return 0; const a = Date.parse(p.start), b = Date.parse(p.end); return b > a ? Math.max(0, Math.min(100, (now-a)/(b-a)*100)) : 0; }
export function readRecent(): string[] { try { const value: unknown = JSON.parse(localStorage.getItem('dzhigga.recent') || '[]'); return Array.isArray(value) ? value.filter((x): x is string => typeof x === 'string').slice(0,20) : []; } catch { return []; } }
export function remember(id: string) { const next = [id,...readRecent().filter(x=>x!==id)].slice(0,20); try {localStorage.setItem('dzhigga.recent',JSON.stringify(next));} catch {} return next; }
