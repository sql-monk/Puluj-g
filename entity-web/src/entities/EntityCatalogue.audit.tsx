import React from 'react'
import ReactDOM from 'react-dom/client'
import Catalogue from './EntityCatalogue'
import TopBar from '../components/TopBar'
import { entityApi } from '../api/entityExtractor'
import { parsePublicHash } from '../public/routes'
import { parseDataQuery } from '../public/query'
import '../index.css'
    (window as any).pending = []; (window as any).states = [];
    const request = (type: string, key: unknown, signal?: AbortSignal) => new Promise<any>((resolve, reject) => (window as any).pending.push({type,key,signal,resolve,reject}));
    entityApi.catalogueMany = (q, signal) => request('list', q, signal);
    entityApi.detail = (_kind,id,signal) => request('detail', id, signal);
    entityApi.history = (_kind,id,signal) => request('history', id, signal);
    function Harness() {
      const [hash, setHash] = React.useState(location.hash);
      const [refresh, setRefresh] = React.useState(0);
      const [, rerender] = React.useState(0);
      const ref = React.useRef(null);
      (window as any).refresh = () => setRefresh(x => x + 1);
      React.useEffect(() => { const changed = () => setHash(location.hash); window.addEventListener('hashchange', changed); return () => window.removeEventListener('hashchange', changed) }, []);
      const route = parsePublicHash(hash).route;
      return React.createElement(React.Fragment, null,
        React.createElement(TopBar, {route, rememberedRoutes:{}, panelOpen:false, onTogglePanel:()=>{}, panelButtonRef:ref, dataStatus:{loading:false}}),
        React.createElement(Catalogue, {route, query:parseDataQuery(route.query).value, refreshKey:String(refresh), onLoadState:(state: unknown) => {(window as any).states.push(state); rerender(x=>x+1)}}));
    }
    ReactDOM.createRoot(document.getElementById('root')!).render(React.createElement(Harness));
