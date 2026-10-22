import type {Session} from './types';
export class ApiError extends Error { constructor(message: string, public status: number) {super(message);} }
let csrf = '';
export function setSession(s: Session) {csrf = s.csrf;}
export async function api<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', cache:'no-store', headers: body === undefined ? {} : {'Content-Type':'application/json','X-DzhiggaTV-CSRF':csrf}, body:body===undefined?undefined:JSON.stringify(body), signal});
  if (!response.ok) {const error = await response.json().catch(()=>({})); throw new ApiError(error.error || 'Нет подключения к DzhiggaTV. Проверьте домашнюю сеть.',response.status);}
  const text = await response.text(); return (text ? JSON.parse(text) : undefined) as T;
}
export function stopOnExit() {
  void fetch('/api/stream/stop',{method:'POST',credentials:'same-origin',keepalive:true,headers:{'Content-Type':'application/json','X-DzhiggaTV-CSRF':csrf},body:'{}'}).catch(()=>{});
}
