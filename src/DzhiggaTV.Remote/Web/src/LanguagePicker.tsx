import {useEffect,useRef,useState} from 'react';
import type {Language} from './i18n';

const choices:{code:Language;name:string}[]=[{code:'ru',name:'Русский'},{code:'en',name:'English'},{code:'zh-Hans',name:'简体中文'}];
function Flag({code}:{code:Language}){
  return <svg viewBox="0 0 30 20" width="24" height="16" aria-hidden="true" focusable="false">
    {code==='ru'?<><path fill="#fff" d="M0 0h30v20H0z"/><path fill="#0039a6" d="M0 6.667h30V20H0z"/><path fill="#d52b1e" d="M0 13.333h30V20H0z"/></>:code==='en'?<><path fill="#012169" d="M0 0h30v20H0z"/><path fill="#fff" d="M0 0h4l26 17v3h-4L0 3zM30 0h-4L0 17v3h4L30 3z"/><path fill="#c8102e" d="M0 0h2l28 18.667V20h-2L0 1.333zM30 0h-2L0 18.667V20h2L30 1.333z"/><path fill="#fff" d="M11 0h8v6h11v8H19v6h-8v-6H0V6h11z"/><path fill="#c8102e" d="M13 0h4v8h13v4H17v8h-4v-8H0V8h13z"/></>:<><path fill="#de2910" d="M0 0h30v20H0z"/>{[[5,5,3],[10,2,1],[12,4,1],[12,7,1],[10,9,1]].map(([x,y,r],index)=>{const rotation=index===0?-Math.PI/2:Math.atan2(5-y,5-x);return <polygon key={index} fill="#ffde00" points={Array.from({length:10},(_,i)=>{const a=rotation+i*Math.PI/5,radius=i%2===0?r:r*.382;return `${x+radius*Math.cos(a)},${y+radius*Math.sin(a)}`;}).join(' ')}/>;})}</>}
  </svg>;
}
export function LanguagePicker({value,label,onChange}:{value:Language;label:string;onChange:(value:Language)=>void}){
  const [open,setOpen]=useState(false),root=useRef<HTMLDivElement>(null),button=useRef<HTMLButtonElement>(null);
  useEffect(()=>{const outside=(e:PointerEvent)=>{if(!root.current?.contains(e.target as Node))setOpen(false);};document.addEventListener('pointerdown',outside);return()=>document.removeEventListener('pointerdown',outside);},[]);
  const choose=(code:Language)=>{onChange(code);setOpen(false);button.current?.focus();};
  return <div className="language-picker" ref={root} onBlur={e=>{if(!e.currentTarget.contains(e.relatedTarget))setOpen(false);}} onKeyDown={e=>{if(e.key==='Escape'){setOpen(false);button.current?.focus();}if(e.key==='ArrowDown'||e.key==='ArrowUp'){e.preventDefault();setOpen(true);queueMicrotask(()=>{const options=[...root.current!.querySelectorAll<HTMLButtonElement>('[role="option"]')];const index=options.indexOf(document.activeElement as HTMLButtonElement);options[(index+(e.key==='ArrowDown'?1:options.length-1)+options.length)%options.length]?.focus();});}}}>
    <button ref={button} type="button" role="combobox" aria-label={label} aria-expanded={open} aria-haspopup="listbox" aria-controls="language-options" onClick={()=>setOpen(!open)}><Flag code={value}/><span>{choices.find(c=>c.code===value)!.name}</span><span aria-hidden="true">⌄</span></button>
    {open&&<div className="language-options" id="language-options" role="listbox" aria-label={label}>{choices.map(choice=><button type="button" role="option" aria-selected={choice.code===value} key={choice.code} onClick={()=>choose(choice.code)}><Flag code={choice.code}/><span>{choice.name}</span>{choice.code===value&&<span aria-hidden="true">✓</span>}</button>)}</div>}
  </div>;
}
