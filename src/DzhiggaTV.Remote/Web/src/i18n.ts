import {useSyncExternalStore} from 'react';
import messages from './messages.json';

export type Language='ru'|'en'|'zh-Hans';
const catalog=messages as Record<string,{En:string;Zh:string}>;
const normalize=(value:unknown):Language=>value==='en'?'en':value==='zh-Hans'||value==='zh-CN'?'zh-Hans':'ru';
function saved():string|null {try{return localStorage.getItem('dzhigga.language');}catch{return null;}}
let language:Language=normalize(saved());
document.documentElement.lang=language==='zh-Hans'?'zh-CN':language;
const listeners=new Set<()=>void>();
export const getLanguage=()=>language;
export const locale=()=>language==='en'?'en-US':language==='zh-Hans'?'zh-CN':'ru-RU';
export function setLanguage(value:unknown,persist=true){
  const code=normalize(value);if(persist)try{localStorage.setItem('dzhigga.language',code);}catch{}
  document.documentElement.lang=code==='zh-Hans'?'zh-CN':code;
  if(language===code)return;language=code;listeners.forEach(listener=>listener());
}
export function serverLanguage(value:unknown){if(!saved())setLanguage(value,false);}
export function useLanguage(){return useSyncExternalStore(callback=>{listeners.add(callback);return()=>{listeners.delete(callback);};},getLanguage,()=> 'ru' as Language);}
export function t(key:string,...values:unknown[]):string{
  const entry=catalog[key],template=language==='ru'?key:language==='en'?entry?.En||key:entry?.Zh||key;
  return values.length?template.replace(/\{(\d+)\}/g,(_,index)=>String(values[Number(index)]??'')):template;
}
