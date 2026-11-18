(() => {
  'use strict';

  const DEFAULT_CENTER = [32.8597, 39.9334];
  const DEFAULT_ZOOM = 6;
  const MAX_TRAIL_POINTS = 500;
  const DRAG_PREVIEW_THROTTLE_MS = 70;

  let activeLayerId = 'google-satellite-hd';
  let editMode = 'none';
  let prevEditModeBeforeMeasure = 'none';
  let mapLoaded = false;
  let hasCenteredOnVehicle = false;
  let followVehicle = false;
  let lastVehicleState = null;
  let internalTrail = [];
  let pendingMission = [];
  let pendingFence = [];
  let pendingRally = [];
  let layersCatalog = {};
  let contextLngLat = null;
  let boundsDragStart = null;
  let activeWaypointSeq = null;
  let selectedSeq = null;
  let measurePoints = [];
  const layerVisibility = {
    trail: true,
    mission: true,
    fence: true,
    markers: true,
    rally: true
  };

  let vehicleMarker = null;
  let vehicleSvgEl = null;
  let vehicleCategoryRendered = null;
  let homeMarker = null;
  let missionMarkers = [];
  let missionMarkerEntries = [];
  let missionMidpointMarkers = [];
  let fenceMarkers = [];
  let rallyMarkers = [];

  const mapEl = document.getElementById('map');
  const hudModeEl = document.getElementById('hud-mode');
  const hudCoordsEl = document.getElementById('hud-coords');
  const hudMeasureEl = document.getElementById('hud-measure');
  const boundsBoxEl = document.getElementById('bounds-box');
  const ctxAnchorEl = document.getElementById('ctx-anchor');
  const contextMenuEl = document.getElementById('context-menu');
  const layersControlEl = document.getElementById('layers-control');
  const layersToggleBtnEl = document.getElementById('layers-toggle-btn');
  const layersPanelEl = document.getElementById('layers-panel');

  function postToHost(payload) {
    try {
      if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
        window.chrome.webview.postMessage(payload);
      }
    } catch (_) {
      // Ignore outside WebView2 host (e.g. unit test / standalone browser smoke check).
    }
  }

  function isValidLatLon(lat, lon) {
    return Number.isFinite(lat) &&
      Number.isFinite(lon) &&
      Math.abs(lat) <= 90 &&
      Math.abs(lon) <= 180 &&
      !(lat === 0 && lon === 0);
  }

  function normalizeCoordinateList(points) {
    if (!Array.isArray(points)) {
      return [];
    }

    const result = [];
    for (const pt of points) {
      if (Array.isArray(pt) && pt.length >= 2) {
        const lon = Number(pt[0]);
        const lat = Number(pt[1]);
        if (isValidLatLon(lat, lon)) {
          result.push([lon, lat]);
        }
      } else if (pt && typeof pt === 'object') {
        const lat = Number(pt.lat ?? pt.latitude);
        const lon = Number(pt.lon ?? pt.lng ?? pt.longitude);
        if (isValidLatLon(lat, lon)) {
          result.push([lon, lat]);
        }
      }
    }
    return result;
  }

  function buildTileUrl(layerId) {
    const encoded = encodeURIComponent(layerId || 'satellite');
    return `./tiles/${encoded}/{z}/{x}/{y}`;
  }

  function normalizeCategoryKey(category) {
    if (typeof category === 'number') {
      switch (category) {
        case 1: return 'fixedwing';
        case 2: return 'multirotor';
        case 3: return 'vtol';
        case 4: return 'rover';
        case 5: return 'boat';
        default: return 'unknown';
      }
    }

    const raw = String(category || 'Unknown').trim().toLowerCase();
    if (raw === '1') return 'fixedwing';
    if (raw === '2') return 'multirotor';
    if (raw === '3') return 'vtol';
    if (raw === '4') return 'rover';
    if (raw === '5') return 'boat';
    return raw;
  }

  function getVehicleSvgMarkup(category) {
    const key = normalizeCategoryKey(category);
    if (key.includes('fixedwing')) {
      return '<svg class="tulpar-vehicle-svg" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg">' +
        '<path d="M16 2 L19 12 L30 18 L30 21 L19 18 L18 25 L22 28 L22 30 L16 28.5 L10 30 L10 28 L14 25 L13 18 L2 21 L2 18 L13 12 Z" ' +
        'fill="#ffffff" stroke="#000000" stroke-width="1.4" stroke-linejoin="round"/></svg>';
    }

    if (key.includes('vtol')) {
      return '<svg class="tulpar-vehicle-svg" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg">' +
        '<path d="M16 3 L18.5 11 L29 15 L29 18 L18.5 16.5 L17.5 25 L21 28 L21 30 L16 28.5 L11 30 L11 28 L14.5 25 L13.5 16.5 L3 18 L3 15 L13.5 11 Z" ' +
        'fill="#ffffff" stroke="#000000" stroke-width="1.2" stroke-linejoin="round"/>' +
        '<circle cx="7" cy="10" r="2.6" fill="#000000" stroke="#ffffff" stroke-width="1.4"/>' +
        '<circle cx="25" cy="10" r="2.6" fill="#000000" stroke="#ffffff" stroke-width="1.4"/>' +
        '<circle cx="7" cy="22" r="2.6" fill="#000000" stroke="#ffffff" stroke-width="1.4"/>' +
        '<circle cx="25" cy="22" r="2.6" fill="#000000" stroke="#ffffff" stroke-width="1.4"/></svg>';
    }

    if (key.includes('multirotor')) {
      return '<svg class="tulpar-vehicle-svg" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg">' +
        '<path d="M8 8 L24 24 M24 8 L8 24" stroke="#000000" stroke-width="3.6" stroke-linecap="round"/>' +
        '<path d="M8 8 L24 24 M24 8 L8 24" stroke="#ffffff" stroke-width="1.8" stroke-linecap="round"/>' +
        '<polygon points="16,5 19.5,14 16,12 12.5,14" fill="#ffffff" stroke="#000000" stroke-width="1.2"/>' +
        '<circle cx="7" cy="7" r="3.4" fill="#000000" stroke="#ffffff" stroke-width="1.5"/>' +
        '<circle cx="25" cy="7" r="3.4" fill="#000000" stroke="#ffffff" stroke-width="1.5"/>' +
        '<circle cx="7" cy="25" r="3.4" fill="#000000" stroke="#ffffff" stroke-width="1.5"/>' +
        '<circle cx="25" cy="25" r="3.4" fill="#000000" stroke="#ffffff" stroke-width="1.5"/></svg>';
    }

    if (key.includes('rover')) {
      return '<svg class="tulpar-vehicle-svg" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg">' +
        '<rect x="9" y="8" width="14" height="18" rx="2" fill="#ffffff" stroke="#000000" stroke-width="1.5"/>' +
        '<rect x="5" y="9" width="3" height="6" fill="#ffffff" stroke="#000000" stroke-width="1.2"/>' +
        '<rect x="24" y="9" width="3" height="6" fill="#ffffff" stroke="#000000" stroke-width="1.2"/>' +
        '<rect x="5" y="19" width="3" height="6" fill="#ffffff" stroke="#000000" stroke-width="1.2"/>' +
        '<rect x="24" y="19" width="3" height="6" fill="#ffffff" stroke="#000000" stroke-width="1.2"/>' +
        '<polygon points="16,3 20,10 12,10" fill="#000000" stroke="#ffffff" stroke-width="1.2"/></svg>';
    }

    if (key.includes('boat')) {
      return '<svg class="tulpar-vehicle-svg" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg">' +
        '<path d="M16 3 L23 13 L21 28 L11 28 L9 13 Z" fill="#ffffff" stroke="#000000" stroke-width="1.5" stroke-linejoin="round"/>' +
        '<line x1="16" y1="8" x2="16" y2="23" stroke="#000000" stroke-width="1.5"/></svg>';
    }

    return '<svg class="tulpar-vehicle-svg" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg">' +
      '<polygon points="16,3 26,27 16,21 6,27" fill="#ffffff" stroke="#000000" stroke-width="1.5" stroke-linejoin="round"/></svg>';
  }

  function createEmptyFeatureCollection() {
    return { type: 'FeatureCollection', features: [] };
  }

  const map = new maplibregl.Map({
    container: 'map',
    center: DEFAULT_CENTER,
    zoom: DEFAULT_ZOOM,
    maxZoom: 22,
    attributionControl: true,
    dragRotate: false,
    pitchWithRotate: false,
    style: {
      version: 8,
      sources: {
        'basemap-tiles': {
          type: 'raster',
          tiles: [buildTileUrl(activeLayerId)],
          tileSize: 256,
          minzoom: 0,
          maxzoom: 21
        }
      },
      layers: [
        {
          id: 'tulpar-bg',
          type: 'background',
          paint: {
            'background-color': '#000000'
          }
        },
        {
          id: 'basemap-layer',
          type: 'raster',
          source: 'basemap-tiles',
          paint: {
            'raster-fade-duration': 100
          }
        }
      ]
    }
  });

  map.addControl(new maplibregl.NavigationControl({ showCompass: true, showZoom: true }), 'top-right');
  map.addControl(new maplibregl.ScaleControl({ maxWidth: 120, unit: 'metric' }), 'bottom-right');

  map.on('load', () => {
    mapLoaded = true;

    map.addSource('tulpar-fence-source', { type: 'geojson', data: createEmptyFeatureCollection() });
    map.addLayer({
      id: 'tulpar-fence-fill',
      type: 'fill',
      source: 'tulpar-fence-source',
      paint: {
        'fill-color': '#ffffff',
        'fill-opacity': 0.08
      }
    });
    map.addLayer({
      id: 'tulpar-fence-outline',
      type: 'line',
      source: 'tulpar-fence-source',
      paint: {
        'line-color': '#ffcc00',
        'line-width': 1.5,
        'line-dasharray': [3, 2]
      }
    });

    map.addSource('tulpar-mission-source', { type: 'geojson', data: createEmptyFeatureCollection() });
    map.addLayer({
      id: 'tulpar-mission-line',
      type: 'line',
      source: 'tulpar-mission-source',
      paint: {
        'line-color': '#f0f0fa',
        'line-width': 2
      }
    });

    map.addSource('tulpar-mission-loop-source', { type: 'geojson', data: createEmptyFeatureCollection() });
    map.addLayer({
      id: 'tulpar-mission-loop-line',
      type: 'line',
      source: 'tulpar-mission-loop-source',
      paint: {
        'line-color': '#38bdf8',
        'line-width': 2,
        'line-dasharray': [4, 3]
      }
    });

    map.addSource('tulpar-trail-source', { type: 'geojson', data: createEmptyFeatureCollection() });
    map.addLayer({
      id: 'tulpar-trail-glow',
      type: 'line',
      source: 'tulpar-trail-source',
      paint: {
        'line-color': '#38bdf8',
        'line-width': 6,
        'line-opacity': 0.22
      }
    });
    map.addLayer({
      id: 'tulpar-trail-line',
      type: 'line',
      source: 'tulpar-trail-source',
      paint: {
        'line-color': '#38bdf8',
        'line-width': 2.2,
        'line-opacity': 0.95,
        'line-dasharray': [2, 1.5]
      }
    });

    map.addSource('tulpar-heading-source', { type: 'geojson', data: createEmptyFeatureCollection() });
    map.addLayer({
      id: 'tulpar-heading-line',
      type: 'line',
      source: 'tulpar-heading-source',
      paint: {
        'line-color': '#ffffff',
        'line-width': 1.5,
        'line-dasharray': [2, 2]
      }
    });

    map.addSource('tulpar-measure-source', { type: 'geojson', data: createEmptyFeatureCollection() });
    map.addLayer({
      id: 'tulpar-measure-line',
      type: 'line',
      source: 'tulpar-measure-source',
      filter: ['==', ['geometry-type'], 'LineString'],
      paint: {
        'line-color': '#ffcc00',
        'line-width': 2.5,
        'line-dasharray': [3, 2]
      }
    });
    map.addLayer({
      id: 'tulpar-measure-points',
      type: 'circle',
      source: 'tulpar-measure-source',
      filter: ['==', ['geometry-type'], 'Point'],
      paint: {
        'circle-radius': 4.5,
        'circle-color': '#ffcc00',
        'circle-stroke-width': 1.5,
        'circle-stroke-color': '#000000'
      }
    });

    applyLayer(activeLayerId);
    refreshLayers();
    applyAllLayerVisibilities();

    if (lastVehicleState) {
      updateVehicle(lastVehicleState);
    }
    if (pendingMission.length > 0) {
      setMission(pendingMission);
    }
    if (pendingFence.length > 0) {
      setFence(pendingFence);
    }
    if (pendingRally.length > 0) {
      setRally(pendingRally);
    }
    if (measurePoints.length > 0) {
      updateMeasureDisplay();
    }
  });

  map.on('mousemove', (e) => {
    const zoom = map.getZoom().toFixed(1);
    hudCoordsEl.textContent = `${e.lngLat.lat.toFixed(6)}, ${e.lngLat.lng.toFixed(6)} · Z${zoom}`;
  });

  map.on('zoomend', () => {
    const c = map.getCenter();
    const zoom = map.getZoom().toFixed(1);
    hudCoordsEl.textContent = `${c.lat.toFixed(6)}, ${c.lng.toFixed(6)} · Z${zoom}`;
  });

  map.on('click', (e) => {
    if (editMode === 'mission') {
      postToHost({ type: 'missionAdd', lat: e.lngLat.lat, lon: e.lngLat.lng });
    } else if (editMode === 'fence') {
      postToHost({ type: 'fenceAdd', lat: e.lngLat.lat, lon: e.lngLat.lng });
    } else if (editMode === 'rally') {
      postToHost({ type: 'rallyAdd', lat: e.lngLat.lat, lon: e.lngLat.lng });
    } else if (editMode === 'measure') {
      addMeasurePoint(e.lngLat.lng, e.lngLat.lat);
    }
  });

  map.on('contextmenu', (e) => {
    if (e.originalEvent) {
      e.originalEvent.preventDefault();
    }
    contextLngLat = e.lngLat;
    openContextMenu(e.originalEvent.clientX, e.originalEvent.clientY);
  });

  // Shift+drag or editMode === 'bounds' rectangle selection for OfflineRegionDownloader
  mapEl.addEventListener('pointerdown', (ev) => {
    const wantsBounds = ev.button === 0 && (editMode === 'bounds' || ev.shiftKey);
    if (!wantsBounds) {
      return;
    }

    ev.preventDefault();
    map.dragPan.disable();
    const rect = mapEl.getBoundingClientRect();
    boundsDragStart = {
      x: ev.clientX - rect.left,
      y: ev.clientY - rect.top
    };
    updateBoundsBox(boundsDragStart.x, boundsDragStart.y, boundsDragStart.x, boundsDragStart.y);
  });

  window.addEventListener('pointermove', (ev) => {
    if (!boundsDragStart) {
      return;
    }
    const rect = mapEl.getBoundingClientRect();
    const x = Math.max(0, Math.min(rect.width, ev.clientX - rect.left));
    const y = Math.max(0, Math.min(rect.height, ev.clientY - rect.top));
    updateBoundsBox(boundsDragStart.x, boundsDragStart.y, x, y);
  });

  window.addEventListener('pointerup', (ev) => {
    if (!boundsDragStart) {
      return;
    }

    const rect = mapEl.getBoundingClientRect();
    const endX = Math.max(0, Math.min(rect.width, ev.clientX - rect.left));
    const endY = Math.max(0, Math.min(rect.height, ev.clientY - rect.top));
    const start = boundsDragStart;
    boundsDragStart = null;
    boundsBoxEl.style.display = 'none';
    map.dragPan.enable();

    if (Math.abs(endX - start.x) < 6 || Math.abs(endY - start.y) < 6) {
      return;
    }

    const sw = map.unproject([Math.min(start.x, endX), Math.max(start.y, endY)]);
    const ne = map.unproject([Math.max(start.x, endX), Math.min(start.y, endY)]);

    postToHost({
      type: 'boundsSelected',
      bounds: {
        minLat: sw.lat,
        minLon: sw.lng,
        maxLat: ne.lat,
        maxLon: ne.lng
      }
    });
  });

  function updateBoundsBox(x1, y1, x2, y2) {
    const left = Math.min(x1, x2);
    const top = Math.min(y1, y2);
    const width = Math.abs(x2 - x1);
    const height = Math.abs(y2 - y1);
    boundsBoxEl.style.display = 'block';
    boundsBoxEl.style.left = `${left}px`;
    boundsBoxEl.style.top = `${top}px`;
    boundsBoxEl.style.width = `${width}px`;
    boundsBoxEl.style.height = `${height}px`;
  }

  function openContextMenu(clientX, clientY) {
    if (!contextMenuEl) {
      return;
    }

    ctxAnchorEl.style.left = `${clientX}px`;
    ctxAnchorEl.style.top = `${clientY}px`;

    const hasPopoverApi = typeof contextMenuEl.showPopover === 'function';
    if (hasPopoverApi) {
      try {
        if (contextMenuEl.matches(':popover-open')) {
          contextMenuEl.hidePopover();
        }
        contextMenuEl.showPopover({ source: ctxAnchorEl });
      } catch (_) {
        contextMenuEl.style.display = 'block';
      }
    } else {
      contextMenuEl.style.display = 'block';
    }

    const supportsImplicitAnchor = hasPopoverApi &&
      CSS.supports('position-area: block-end') &&
      typeof ToggleEvent !== 'undefined' &&
      'source' in ToggleEvent.prototype;

    if (!supportsImplicitAnchor) {
      const rect = contextMenuEl.getBoundingClientRect();
      const clampedLeft = Math.min(clientX, Math.max(8, window.innerWidth - rect.width - 8));
      const clampedTop = Math.min(clientY, Math.max(8, window.innerHeight - rect.height - 8));
      contextMenuEl.style.left = `${clampedLeft}px`;
      contextMenuEl.style.top = `${clampedTop}px`;
    }
  }

  function closeContextMenu() {
    if (!contextMenuEl) {
      return;
    }

    if (typeof contextMenuEl.hidePopover === 'function') {
      try {
        if (contextMenuEl.matches(':popover-open')) {
          contextMenuEl.hidePopover();
        }
      } catch (_) {
        contextMenuEl.style.display = 'none';
      }
    } else {
      contextMenuEl.style.display = 'none';
    }
  }

  if (contextMenuEl) {
    contextMenuEl.addEventListener('click', (ev) => {
      const btn = ev.target.closest('button[data-action]');
      if (!btn) {
        return;
      }

      const action = btn.dataset.action;
      closeContextMenu();

      if (action === 'center-vehicle') {
        centerOnVehicle();
        return;
      }

      if (action === 'measure-toggle') {
        toggleMeasureMode();
        return;
      }

      if (action === 'measure-clear') {
        clearMeasure();
        return;
      }

      if (!contextLngLat) {
        return;
      }

      if (action === 'guided') {
        postToHost({ type: 'guidedTarget', lat: contextLngLat.lat, lon: contextLngLat.lng });
      } else if (action === 'mission-add') {
        postToHost({ type: 'missionAdd', lat: contextLngLat.lat, lon: contextLngLat.lng });
      } else if (action === 'fence-add') {
        postToHost({ type: 'fenceAdd', lat: contextLngLat.lat, lon: contextLngLat.lng });
      } else if (action === 'rally-add') {
        postToHost({ type: 'rallyAdd', lat: contextLngLat.lat, lon: contextLngLat.lng });
      }
    });
  }

  if (!('focusGroup' in HTMLElement.prototype)) {
    document.querySelectorAll('[focusgroup~="menu"]').forEach((group) => {
      group.setAttribute('role', 'menu');
      group.querySelectorAll('button:not([role])').forEach((item) => item.setAttribute('role', 'menuitem'));
      group.addEventListener('keydown', (event) => {
        if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
          return;
        }
        const items = [...group.querySelectorAll('button')].filter((item) => !item.disabled);
        const current = items.indexOf(document.activeElement);
        if (items.length === 0) {
          return;
        }
        let next = 0;
        if (event.key === 'Home') {
          next = 0;
        } else if (event.key === 'End') {
          next = items.length - 1;
        } else if (current >= 0) {
          next = (current + (event.key === 'ArrowUp' ? -1 : 1) + items.length) % items.length;
        }
        event.preventDefault();
        items[next].focus({ preventScroll: true });
      }, true);
    });
  }

  function applyLayer(layerId) {
    activeLayerId = layerId || 'google-satellite-hd';
    if (!mapLoaded) {
      return;
    }

    const layerInfo = layersCatalog[activeLayerId];
    const minZoom = Number.isFinite(Number(layerInfo?.minZoom)) ? Number(layerInfo.minZoom) : 0;
    const defaultMaxZoom = activeLayerId.includes('-hd') ? 21 : 19;
    const maxZoom = Number.isFinite(Number(layerInfo?.maxZoom)) ? Number(layerInfo.maxZoom) : defaultMaxZoom;
    const tileUrl = buildTileUrl(activeLayerId);

    const src = map.getSource('basemap-tiles');
    if (src && src.minzoom === minZoom && src.maxzoom === maxZoom && typeof src.setTiles === 'function') {
      src.setTiles([tileUrl]);
      return;
    }

    if (map.getLayer('basemap-layer')) {
      map.removeLayer('basemap-layer');
    }
    if (map.getSource('basemap-tiles')) {
      map.removeSource('basemap-tiles');
    }

    map.addSource('basemap-tiles', {
      type: 'raster',
      tiles: [tileUrl],
      tileSize: 256,
      minzoom: minZoom,
      maxzoom: maxZoom
    });

    const beforeId = map.getLayer('tulpar-fence-fill') ? 'tulpar-fence-fill' : undefined;
    map.addLayer({
      id: 'basemap-layer',
      type: 'raster',
      source: 'basemap-tiles',
      paint: {
        'raster-fade-duration': 100
      }
    }, beforeId);
  }

  function setLayer(layerId) {
    applyLayer(layerId);
  }

  async function refreshLayers() {
    try {
      const resp = await fetch('./api/layers');
      if (!resp.ok) {
        return;
      }
      const list = await resp.json();
      const catalog = {};
      if (Array.isArray(list)) {
        for (const item of list) {
          if (item && item.id) {
            catalog[item.id] = item;
          }
        }
      }
      layersCatalog = catalog;
      if (mapLoaded) {
        applyLayer(activeLayerId);
      }
    } catch (_) {
      // Offline / transient fetch failure; local tile endpoint remains usable.
    }
  }

  function updateVehicle(state) {
    if (!state || typeof state !== 'object') {
      return;
    }

    lastVehicleState = state;
    if (!mapLoaded) {
      return;
    }

    const lat = Number(state.lat);
    const lon = Number(state.lon);
    const headingDeg = Number.isFinite(Number(state.headingDeg)) ? Number(state.headingDeg) : 0;
    const category = state.category || 'Unknown';

    if (isValidLatLon(lat, lon)) {
      if (!vehicleMarker) {
        const el = document.createElement('div');
        el.className = 'tulpar-vehicle-marker';
        el.innerHTML = getVehicleSvgMarkup(category);
        vehicleSvgEl = el.firstElementChild;
        vehicleCategoryRendered = category;
        vehicleMarker = new maplibregl.Marker({ element: el, anchor: 'center' })
          .setLngLat([lon, lat])
          .addTo(map);
      } else {
        if (vehicleCategoryRendered !== category) {
          const el = vehicleMarker.getElement();
          el.innerHTML = getVehicleSvgMarkup(category);
          vehicleSvgEl = el.firstElementChild;
          vehicleCategoryRendered = category;
        }
        vehicleMarker.setLngLat([lon, lat]);
      }

      if (vehicleSvgEl) {
        vehicleSvgEl.style.transform = `rotate(${headingDeg}deg)`;
      }

      if (!hasCenteredOnVehicle) {
        hasCenteredOnVehicle = true;
        map.jumpTo({ center: [lon, lat], zoom: Math.max(map.getZoom(), 15) });
      } else if (followVehicle) {
        map.easeTo({ center: [lon, lat], duration: 180 });
      }

      // Heading vector (~220m projection ahead of vehicle)
      const rad = (headingDeg * Math.PI) / 180;
      const dLat = (0.002 * Math.cos(rad));
      const cosLat = Math.max(0.15, Math.cos((lat * Math.PI) / 180));
      const dLon = (0.002 * Math.sin(rad)) / cosLat;
      const headingSrc = map.getSource('tulpar-heading-source');
      if (headingSrc) {
        headingSrc.setData({
          type: 'Feature',
          geometry: {
            type: 'LineString',
            coordinates: [[lon, lat], [lon + dLon, lat + dLat]]
          }
        });
      }

      // Flight trail polyline (max 500 points)
      let coords;
      if (Array.isArray(state.trail)) {
        coords = normalizeCoordinateList(state.trail).slice(-MAX_TRAIL_POINTS);
        internalTrail = coords.slice();
      } else {
        const prev = internalTrail[internalTrail.length - 1];
        if (!prev || Math.abs(prev[0] - lon) > 1e-6 || Math.abs(prev[1] - lat) > 1e-6) {
          internalTrail.push([lon, lat]);
          if (internalTrail.length > MAX_TRAIL_POINTS) {
            internalTrail.splice(0, internalTrail.length - MAX_TRAIL_POINTS);
          }
        }
        coords = internalTrail;
      }

      const trailSrc = map.getSource('tulpar-trail-source');
      if (trailSrc) {
        trailSrc.setData(coords.length >= 2
          ? { type: 'Feature', geometry: { type: 'LineString', coordinates: coords } }
          : createEmptyFeatureCollection());
      }
    }

    // Home marker
    const homeLat = Number(state.homeLat);
    const homeLon = Number(state.homeLon);
    if (state.hasHome && isValidLatLon(homeLat, homeLon)) {
      if (!homeMarker) {
        const homeEl = document.createElement('div');
        homeEl.className = 'tulpar-home-marker';
        homeEl.textContent = 'H';
        homeMarker = new maplibregl.Marker({ element: homeEl, anchor: 'center' })
          .setLngLat([homeLon, homeLat])
          .addTo(map);
      } else {
        homeMarker.setLngLat([homeLon, homeLat]);
      }
    } else if (homeMarker) {
      homeMarker.remove();
      homeMarker = null;
    }

    if ('activeWaypointSeq' in state) {
      setActiveWaypoint(state.activeWaypointSeq);
    }
  }

  function setActiveWaypoint(seq) {
    const parsed = (seq === null || seq === undefined || seq === '') ? null : Number(seq);
    activeWaypointSeq = (Number.isFinite(parsed) && parsed >= 0) ? Math.round(parsed) : null;
    for (const entry of missionMarkerEntries) {
      entry.el.classList.toggle('is-active', activeWaypointSeq !== null && entry.seq === activeWaypointSeq);
    }
  }

  function setSelectedWaypoint(seq) {
    const parsed = (seq === null || seq === undefined || seq === '') ? null : Number(seq);
    selectedSeq = (Number.isFinite(parsed) && parsed >= 0) ? Math.round(parsed) : null;
    for (const entry of missionMarkerEntries) {
      entry.el.classList.toggle('is-selected', selectedSeq !== null && entry.seq === selectedSeq);
    }
  }

  function setMapLayerVisibility(layerId, visible) {
    if (!mapLoaded || !map.getLayer(layerId)) {
      return;
    }
    map.setLayoutProperty(layerId, 'visibility', visible ? 'visible' : 'none');
  }

  function applyLayerGroupVisibility(group) {
    const visible = layerVisibility[group] !== false;
    if (group === 'trail') {
      setMapLayerVisibility('tulpar-trail-glow', visible);
      setMapLayerVisibility('tulpar-trail-line', visible);
    } else if (group === 'mission') {
      setMapLayerVisibility('tulpar-mission-line', visible);
      setMapLayerVisibility('tulpar-mission-loop-line', visible);
    } else if (group === 'fence') {
      setMapLayerVisibility('tulpar-fence-fill', visible);
      setMapLayerVisibility('tulpar-fence-outline', visible);
      for (const marker of fenceMarkers) {
        marker.getElement().style.display = visible ? '' : 'none';
      }
    } else if (group === 'markers') {
      for (const marker of missionMarkers) {
        marker.getElement().style.display = visible ? '' : 'none';
      }
      for (const midMarker of missionMidpointMarkers) {
        midMarker.getElement().style.display = visible ? '' : 'none';
      }
    } else if (group === 'rally') {
      for (const marker of rallyMarkers) {
        marker.getElement().style.display = visible ? '' : 'none';
      }
    }
  }

  function applyAllLayerVisibilities() {
    applyLayerGroupVisibility('trail');
    applyLayerGroupVisibility('mission');
    applyLayerGroupVisibility('fence');
    applyLayerGroupVisibility('markers');
    applyLayerGroupVisibility('rally');
  }

  if (layersToggleBtnEl && layersPanelEl) {
    layersToggleBtnEl.addEventListener('click', (ev) => {
      ev.stopPropagation();
      const willOpen = layersPanelEl.hidden;
      layersPanelEl.hidden = !willOpen;
      layersToggleBtnEl.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
    });

    layersPanelEl.addEventListener('click', (ev) => {
      ev.stopPropagation();
    });

    layersPanelEl.addEventListener('change', (ev) => {
      const checkbox = ev.target.closest('input[type="checkbox"][data-layer-group]');
      if (!checkbox) {
        return;
      }
      const group = checkbox.dataset.layerGroup;
      if (group in layerVisibility) {
        layerVisibility[group] = checkbox.checked;
        applyLayerGroupVisibility(group);
      }
    });

    document.addEventListener('click', (ev) => {
      if (!layersPanelEl.hidden && layersControlEl && !layersControlEl.contains(ev.target)) {
        layersPanelEl.hidden = true;
        layersToggleBtnEl.setAttribute('aria-expanded', 'false');
      }
    });

    document.addEventListener('keydown', (ev) => {
      if (ev.key === 'Escape' && !layersPanelEl.hidden) {
        layersPanelEl.hidden = true;
        layersToggleBtnEl.setAttribute('aria-expanded', 'false');
      }
    });
  }

  function setMission(items) {
    pendingMission = Array.isArray(items) ? items : [];
    if (!mapLoaded) {
      return;
    }

    for (const marker of missionMarkers) {
      marker.remove();
    }
    for (const midMarker of missionMidpointMarkers) {
      midMarker.remove();
    }
    missionMarkers = [];
    missionMarkerEntries = [];
    missionMidpointMarkers = [];

    const spatialWaypoints = [];
    const coordBySeq = {};
    const jumpEdges = [];
    const midpointEntries = [];

    function updateMissionGeometry() {
      const lineCoords = spatialWaypoints.map((wp) => wp.coord);
      const missionSrc = map.getSource('tulpar-mission-source');
      if (missionSrc) {
        missionSrc.setData(lineCoords.length >= 2
          ? { type: 'Feature', geometry: { type: 'LineString', coordinates: lineCoords } }
          : createEmptyFeatureCollection());
      }

      const loopFeatures = [];
      for (const edge of jumpEdges) {
        const fromWp = spatialWaypoints[edge.fromIndex];
        const targetCoord = coordBySeq[edge.targetSeq];
        if (fromWp && targetCoord) {
          loopFeatures.push({
            type: 'Feature',
            geometry: { type: 'LineString', coordinates: [fromWp.coord, targetCoord] }
          });
        }
      }

      const loopSrc = map.getSource('tulpar-mission-loop-source');
      if (loopSrc) {
        loopSrc.setData({ type: 'FeatureCollection', features: loopFeatures });
      }

      for (const mid of midpointEntries) {
        mid.marker.setLngLat([
          (mid.leftWp.coord[0] + mid.rightWp.coord[0]) / 2,
          (mid.leftWp.coord[1] + mid.rightWp.coord[1]) / 2
        ]);
      }
    }

    for (let i = 0; i < pendingMission.length; i++) {
      const item = pendingMission[i];
      if (!item || typeof item !== 'object') {
        continue;
      }

      const seq = Number.isFinite(Number(item.seq)) ? Number(item.seq) : i;
      const cmdId = Number(item.command);
      if (cmdId === 177 && spatialWaypoints.length > 0) { // MAV_CMD_DO_JUMP
        jumpEdges.push({
          fromIndex: spatialWaypoints.length - 1,
          targetSeq: Math.round(Number(item.param1))
        });
        continue;
      }

      const lat = Number(item.lat ?? item.latitude);
      const lon = Number(item.lon ?? item.lng ?? item.longitude);
      if (!isValidLatLon(lat, lon)) {
        continue;
      }

      const coord = [lon, lat];
      coordBySeq[seq] = coord;
      const altVal = Number(item.alt ?? item.altitude);
      const altLabel = Number.isFinite(altVal) ? ` · ${Math.round(altVal)}m` : '';

      const el = document.createElement('div');
      el.className = 'tulpar-wp-marker';
      if (activeWaypointSeq !== null && seq === activeWaypointSeq) {
        el.classList.add('is-active');
      }
      if (selectedSeq !== null && seq === selectedSeq) {
        el.classList.add('is-selected');
      }
      if (!layerVisibility.markers) {
        el.style.display = 'none';
      }
      el.textContent = String(seq);
      el.title = `WP #${seq}${altLabel} — Ayarlar için tıkla`;

      let downX = 0;
      let downY = 0;
      el.addEventListener('pointerdown', (ev) => {
        downX = ev.clientX;
        downY = ev.clientY;
        ev.stopPropagation();
      });
      el.addEventListener('click', (ev) => {
        ev.stopPropagation();
        const dist = Math.hypot(ev.clientX - downX, ev.clientY - downY);
        if (dist < 6) {
          setSelectedWaypoint(seq);
          postToHost({ type: 'missionSelect', seq });
        }
      });

      const marker = new maplibregl.Marker({ element: el, draggable: true, anchor: 'center' })
        .setLngLat(coord)
        .addTo(map);

      const wpEntry = { seq, coord, marker, el };
      spatialWaypoints.push(wpEntry);

      let lastDragPreviewAt = 0;
      marker.on('drag', () => {
        const pos = marker.getLngLat();
        wpEntry.coord = [pos.lng, pos.lat];
        coordBySeq[seq] = wpEntry.coord;
        updateMissionGeometry();

        const now = performance.now();
        if (now - lastDragPreviewAt >= DRAG_PREVIEW_THROTTLE_MS) {
          lastDragPreviewAt = now;
          postToHost({ type: 'missionMovePreview', seq, lat: pos.lat, lon: pos.lng });
        }
      });

      marker.on('dragend', () => {
        const pos = marker.getLngLat();
        wpEntry.coord = [pos.lng, pos.lat];
        coordBySeq[seq] = wpEntry.coord;
        updateMissionGeometry();
        postToHost({ type: 'missionMove', seq, lat: pos.lat, lon: pos.lng });
      });

      missionMarkers.push(marker);
      missionMarkerEntries.push(wpEntry);
    }

    for (let k = 0; k < spatialWaypoints.length - 1; k++) {
      const leftWp = spatialWaypoints[k];
      const rightWp = spatialWaypoints[k + 1];
      const midLon = (leftWp.coord[0] + rightWp.coord[0]) / 2;
      const midLat = (leftWp.coord[1] + rightWp.coord[1]) / 2;

      const midEl = document.createElement('div');
      midEl.className = 'tulpar-wp-midpoint';
      if (!layerVisibility.markers) {
        midEl.style.display = 'none';
      }
      midEl.textContent = '+';
      midEl.title = `WP #${leftWp.seq} sonrasına nokta ekle`;
      midEl.addEventListener('pointerdown', (ev) => ev.stopPropagation());

      const midMarker = new maplibregl.Marker({ element: midEl, anchor: 'center' })
        .setLngLat([midLon, midLat])
        .addTo(map);

      midEl.addEventListener('click', (ev) => {
        ev.stopPropagation();
        const pos = midMarker.getLngLat();
        postToHost({
          type: 'missionInsertAfter',
          afterSeq: leftWp.seq,
          lat: pos.lat,
          lon: pos.lng
        });
      });

      missionMidpointMarkers.push(midMarker);
      midpointEntries.push({ leftWp, rightWp, marker: midMarker });
    }

    updateMissionGeometry();
  }

  function setFence(points) {
    pendingFence = Array.isArray(points) ? points : [];
    if (!mapLoaded) {
      return;
    }

    for (const marker of fenceMarkers) {
      marker.remove();
    }
    fenceMarkers = [];

    const coords = normalizeCoordinateList(pendingFence);
    for (let i = 0; i < coords.length; i++) {
      const [lon, lat] = coords[i];
      const idx = i;
      const el = document.createElement('div');
      el.className = 'tulpar-fence-marker';
      if (!layerVisibility.fence) {
        el.style.display = 'none';
      }
      el.title = `GF ${idx}`;
      el.addEventListener('click', (ev) => ev.stopPropagation());
      el.addEventListener('pointerdown', (ev) => ev.stopPropagation());

      const marker = new maplibregl.Marker({ element: el, draggable: true, anchor: 'center' })
        .setLngLat([lon, lat])
        .addTo(map);

      marker.on('dragend', () => {
        const pos = marker.getLngLat();
        postToHost({ type: 'fenceMove', index: idx, lat: pos.lat, lon: pos.lng });
      });

      fenceMarkers.push(marker);
    }

    const fenceSrc = map.getSource('tulpar-fence-source');
    if (fenceSrc) {
      if (coords.length >= 3) {
        const closedRing = [...coords, coords[0]];
        fenceSrc.setData({
          type: 'Feature',
          geometry: { type: 'Polygon', coordinates: [closedRing] }
        });
      } else if (coords.length === 2) {
        fenceSrc.setData({
          type: 'Feature',
          geometry: { type: 'LineString', coordinates: coords }
        });
      } else {
        fenceSrc.setData(createEmptyFeatureCollection());
      }
    }
  }

  function setRally(points) {
    pendingRally = Array.isArray(points) ? points : [];
    if (!mapLoaded) {
      return;
    }

    for (const marker of rallyMarkers) {
      marker.remove();
    }
    rallyMarkers = [];

    for (let i = 0; i < pendingRally.length; i++) {
      const pt = pendingRally[i];
      if (!pt || typeof pt !== 'object') {
        continue;
      }
      const lat = Number(pt.lat ?? pt.latitude);
      const lon = Number(pt.lon ?? pt.lng ?? pt.longitude);
      if (!isValidLatLon(lat, lon)) {
        continue;
      }

      const idx = i;
      const altVal = Number(pt.alt ?? pt.altitude);
      const el = document.createElement('div');
      el.className = 'tulpar-rally-marker';
      if (!layerVisibility.rally) {
        el.style.display = 'none';
      }
      el.textContent = `R${idx}`;
      el.title = `Rally #${idx}${Number.isFinite(altVal) ? ` · ${Math.round(altVal)}m` : ''}`;
      el.addEventListener('click', (ev) => ev.stopPropagation());
      el.addEventListener('pointerdown', (ev) => ev.stopPropagation());

      const marker = new maplibregl.Marker({ element: el, draggable: true, anchor: 'center' })
        .setLngLat([lon, lat])
        .addTo(map);

      marker.on('dragend', () => {
        const pos = marker.getLngLat();
        postToHost({ type: 'rallyMove', index: idx, lat: pos.lat, lon: pos.lng });
      });

      rallyMarkers.push(marker);
    }
  }

  function haversineMeters(lon1, lat1, lon2, lat2) {
    const R = 6371000;
    const toRad = Math.PI / 180;
    const dLat = (lat2 - lat1) * toRad;
    const dLon = (lon2 - lon1) * toRad;
    const a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
      Math.cos(lat1 * toRad) * Math.cos(lat2 * toRad) *
      Math.sin(dLon / 2) * Math.sin(dLon / 2);
    return 2 * R * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  }

  function formatDistance(meters) {
    if (meters >= 1000) {
      return `${(meters / 1000).toFixed(2)} km`;
    }
    return `${Math.round(meters)} m`;
  }

  function updateMeasureDisplay() {
    if (mapLoaded) {
      const measureSrc = map.getSource('tulpar-measure-source');
      if (measureSrc) {
        const features = measurePoints.map((coord) => ({
          type: 'Feature',
          geometry: { type: 'Point', coordinates: coord }
        }));
        if (measurePoints.length >= 2) {
          features.push({
            type: 'Feature',
            geometry: { type: 'LineString', coordinates: measurePoints }
          });
        }
        measureSrc.setData({ type: 'FeatureCollection', features });
      }
    }

    if (!hudMeasureEl) {
      return;
    }

    if (measurePoints.length === 0 && editMode !== 'measure') {
      hudMeasureEl.hidden = true;
      return;
    }

    hudMeasureEl.hidden = false;
    if (measurePoints.length === 0) {
      hudMeasureEl.textContent = 'ÖLÇÜM: NOKTA SEÇİN';
      return;
    }

    if (measurePoints.length === 1) {
      hudMeasureEl.textContent = 'ÖLÇÜM: 0 m (1 NOKTA)';
      return;
    }

    let totalMeters = 0;
    for (let i = 1; i < measurePoints.length; i++) {
      const [lon1, lat1] = measurePoints[i - 1];
      const [lon2, lat2] = measurePoints[i];
      totalMeters += haversineMeters(lon1, lat1, lon2, lat2);
    }
    hudMeasureEl.textContent = `ÖLÇÜM: ${formatDistance(totalMeters)}`;
  }

  function addMeasurePoint(lon, lat) {
    if (!isValidLatLon(lat, lon)) {
      return;
    }
    measurePoints.push([lon, lat]);
    updateMeasureDisplay();
  }

  function clearMeasure() {
    measurePoints = [];
    updateMeasureDisplay();
  }

  function toggleMeasureMode() {
    if (editMode === 'measure') {
      const restoreMode = prevEditModeBeforeMeasure === 'measure' ? 'none' : prevEditModeBeforeMeasure;
      setEditMode(restoreMode);
    } else {
      prevEditModeBeforeMeasure = editMode;
      setEditMode('measure');
    }
  }

  function setEditMode(mode) {
    const normalized = String(mode || 'none').toLowerCase();
    editMode = ['none', 'mission', 'fence', 'rally', 'bounds', 'measure'].includes(normalized) ? normalized : 'none';

    mapEl.classList.toggle('mode-crosshair', editMode !== 'none');
    hudModeEl.dataset.editing = editMode !== 'none' ? 'true' : 'false';

    if (editMode === 'mission') {
      hudModeEl.textContent = 'GÖREV EKLEME';
    } else if (editMode === 'fence') {
      hudModeEl.textContent = 'GEOFENCE ÇİZİM';
    } else if (editMode === 'rally') {
      hudModeEl.textContent = 'RALLY NOKTASI EKLE';
    } else if (editMode === 'bounds') {
      hudModeEl.textContent = 'BÖLGE SEÇİMİ';
    } else if (editMode === 'measure') {
      hudModeEl.textContent = 'MESAFE ÖLÇÜMÜ';
    } else {
      hudModeEl.textContent = 'İZLEME';
    }

    updateMeasureDisplay();
  }

  function centerOnVehicle() {
    if (lastVehicleState && isValidLatLon(Number(lastVehicleState.lat), Number(lastVehicleState.lon))) {
      map.easeTo({
        center: [Number(lastVehicleState.lon), Number(lastVehicleState.lat)],
        duration: 350
      });
    }
  }

  function setFollowVehicle(enabled) {
    followVehicle = Boolean(enabled);
    if (followVehicle) {
      centerOnVehicle();
    }
  }

  if (window.chrome && window.chrome.webview && typeof window.chrome.webview.addEventListener === 'function') {
    window.chrome.webview.addEventListener('message', (event) => {
      const msg = event.data;
      if (!msg || typeof msg !== 'object') {
        return;
      }

      switch (msg.type) {
        case 'setLayer':
          setLayer(msg.layerId ?? msg.payload);
          break;
        case 'updateVehicle':
          updateVehicle(msg.payload ?? msg);
          break;
        case 'setMission':
          setMission(msg.items ?? msg.payload);
          break;
        case 'setActiveWaypoint':
          setActiveWaypoint(msg.payload?.seq ?? msg.seq ?? msg.payload);
          break;
        case 'setSelectedWaypoint':
          setSelectedWaypoint(msg.payload?.seq ?? msg.seq ?? msg.payload);
          break;
        case 'setFence':
          setFence(msg.points ?? msg.payload);
          break;
        case 'setRally':
          setRally(msg.points ?? msg.payload);
          break;
        case 'setEditMode':
          setEditMode(msg.mode ?? msg.payload);
          break;
        case 'clearMeasure':
          clearMeasure();
          break;
        case 'centerOnVehicle':
          centerOnVehicle();
          break;
        case 'setFollowVehicle':
          setFollowVehicle(msg.enabled ?? msg.payload);
          break;
        case 'refreshLayers':
          refreshLayers();
          break;
      }
    });
  }

  window.tulparMap = {
    setLayer,
    updateVehicle,
    setMission,
    setActiveWaypoint,
    setSelectedWaypoint,
    setFence,
    setRally,
    setEditMode,
    clearMeasure,
    centerOnVehicle,
    setFollowVehicle,
    refreshLayers,
    getCatalog: () => layersCatalog
  };
})();
