// 服务端与浏览器共享的运动学常量，不依赖 Three.js 或 DOM。
export const LIMITS = Object.freeze({
  x: Object.freeze([-100, 100]),
  y: Object.freeze([-100, 100]),
  z: Object.freeze([0, 60]),
});
