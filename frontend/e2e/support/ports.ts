/** Ports used only by the browser tests, so they never collide with `npm run dev` (5173) or the API (5080). */
export const PORTS = { mock: 9101, api: 5081, app: 5174 } as const;

export const API_URL = `http://localhost:${PORTS.api}`;
export const MOCK_URL = `http://127.0.0.1:${PORTS.mock}`;
