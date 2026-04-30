import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { TransformControls } from "three/addons/controls/TransformControls.js";

const MODEL_URL = "../../ct%20sas/source/sas%20blue.glb";
const CANONICAL_URL = "../../tools/cs2_player_hitboxes_canonical.json";
const TUNED_URL = "../../tools/sas_blue_tools_preview_los_points.json";
const TARGET_ANIMATION = "tools_preview_sas blue";
const FALLBACK_ANIMATION_INDEX = 364;
const DEFAULT_UNIT_SCALE = 36;

const canvas = document.querySelector("#viewport");
const labelLayer = document.querySelector("#labels");
const statusEl = document.querySelector("#status");
const pointSelect = document.querySelector("#pointSelect");
const pointName = document.querySelector("#pointName");
const coordX = document.querySelector("#coordX");
const coordY = document.querySelector("#coordY");
const coordZ = document.querySelector("#coordZ");
const fineMode = document.querySelector("#fineMode");
const orbitEnabled = document.querySelector("#orbitEnabled");
const exportDrawer = document.querySelector("#exportDrawer");
const exportText = document.querySelector("#exportText");
const exportKind = document.querySelector("#exportKind");
const autoGlbMatch = document.querySelector("#autoGlbMatch");
const addCustomPointButton = document.querySelector("#addCustomPoint");
const deleteCustomPointButton = document.querySelector("#deleteCustomPoint");
const resetCustomButton = document.querySelector("#resetCustom");
const focusSelectedButton = document.querySelector("#focusSelected");
const modelOpacity = document.querySelector("#modelOpacity");

let unitScale = DEFAULT_UNIT_SCALE;
let selectedPoint = null;
let hoveredPoint = null;
let axisLock = "free";
let canonicalPoints = [];
let points = [];
let mixer = null;
let dragState = null;
let restBoneMatrices = new Map();
let posedBoneMatrices = new Map();
let modelMaterials = [];
let audioContext = null;
let focusedPoint = null;
let preFocusCameraState = null;
let animationStarted = false;

const renderer = new THREE.WebGLRenderer({
  canvas,
  antialias: true,
  alpha: false
});
renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.28;

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x252b30);

const camera = new THREE.PerspectiveCamera(42, window.innerWidth / window.innerHeight, 0.01, 100);
camera.position.set(2.8, 1.55, 3.1);

const orbit = new OrbitControls(camera, renderer.domElement);
orbit.target.set(0, 0.95, 0);
orbit.enableDamping = true;
orbit.enableZoom = true;
orbit.zoomSpeed = 1.15;
orbit.minDistance = 0.55;
orbit.maxDistance = 8.0;
orbit.mouseButtons = {
  LEFT: null,
  MIDDLE: THREE.MOUSE.DOLLY,
  RIGHT: THREE.MOUSE.ROTATE
};
orbit.touches = {
  ONE: THREE.TOUCH.ROTATE,
  TWO: THREE.TOUCH.DOLLY_PAN
};

const raycaster = new THREE.Raycaster();
const pointer = new THREE.Vector2();
const dragIntersection = new THREE.Vector3();
const pointGroup = new THREE.Group();
const modelRoot = new THREE.Group();
const grid = new THREE.GridHelper(2.4, 24, 0x273137, 0x171d21);
grid.position.y = 0;
grid.material.opacity = 0.55;
grid.material.transparent = true;
pointGroup.renderOrder = 1000;

scene.add(modelRoot);
scene.add(pointGroup);
scene.add(grid);

const ambientLight = new THREE.AmbientLight(0xffffff, 0.55);
scene.add(ambientLight);
scene.add(new THREE.HemisphereLight(0xe4f4ff, 0x2d3338, 2.15));
const keyLight = new THREE.DirectionalLight(0xffffff, 3.2);
keyLight.position.set(2.8, 4, 2.4);
scene.add(keyLight);
const fillLight = new THREE.DirectionalLight(0xd7ecff, 1.7);
fillLight.position.set(-2.6, 2.2, 1.8);
scene.add(fillLight);
const rimLight = new THREE.DirectionalLight(0x8ee4ff, 1.55);
rimLight.position.set(-2.4, 2.5, -2.8);
scene.add(rimLight);

const transformControls = new TransformControls(camera, renderer.domElement);
transformControls.setMode("translate");
transformControls.setSpace("world");
transformControls.setSize(0.62);
transformControls.addEventListener("dragging-changed", (event) => {
  orbit.enabled = orbitEnabled.checked && !event.value;
});
transformControls.addEventListener("objectChange", () => {
  if (!selectedPoint) {
    return;
  }
  syncPointFromMesh(selectedPoint);
  updateSelectionPanel();
  updateExportPreview();
});
scene.add(transformControls);

init();

async function init() {
  setStatus("Loading data");
  const [canonical, tuned] = await Promise.all([
    fetchJson(CANONICAL_URL),
    fetchJson(TUNED_URL).catch(() => null)
  ]);

  canonicalPoints = buildCanonicalPoints(canonical);
  points = mergeTunedPoints(canonicalPoints, tuned);
  buildPointMeshes();
  populatePointSelect();
  selectPoint(points[0]);
  updateExportPreview();
  startAnimationLoop();

  setStatus("Loading model");
  try {
    await loadModel();
    setStatus(`Ready: ${points.length} points`);
  } catch (error) {
    setStatus(`Model failed: ${error.message}`);
  }
}

async function fetchJson(url) {
  const response = await fetch(url, { cache: "no-cache" });
  if (!response.ok) {
    throw new Error(`Failed to load ${url}: ${response.status}`);
  }
  return response.json();
}

function buildCanonicalPoints(canonical) {
  return canonical.primitives.map((primitive) => {
    const localPoint = midpoint(primitive.local_point0, primitive.local_point1);
    return {
      index: primitive.index,
      name: primitive.name,
      bone: primitive.bone,
      useFixedHeadOrigin: Boolean(primitive.use_fixed_head_origin),
      canonicalMidpoint: localPoint,
      localPoint: [...localPoint],
      custom: false
    };
  });
}

function mergeTunedPoints(canonical, tuned) {
  const tunedByIndex = new Map();
  if (tuned?.points?.length) {
    for (const point of tuned.points) {
      tunedByIndex.set(point.index, point);
    }
  }

  const merged = canonical.map((base) => {
    const tunedPoint = tunedByIndex.get(base.index);
    const localPoint = Array.isArray(tunedPoint?.local_point) && tunedPoint.local_point.length === 3
      ? tunedPoint.local_point.map(Number)
      : base.localPoint;
    return {
      ...base,
      requiredWeaponClass: String(tunedPoint?.required_weapon_class || ""),
      useFixedHeadOrigin: Boolean(tunedPoint?.use_fixed_head_origin ?? base.useFixedHeadOrigin),
      localPoint: localPoint.map((value) => roundSource(value)),
      custom: false
    };
  });

  if (tuned?.points?.length > canonical.length) {
    for (const tunedPoint of tuned.points.slice(canonical.length)) {
      const localPoint = Array.isArray(tunedPoint.local_point) && tunedPoint.local_point.length === 3
        ? tunedPoint.local_point.map((value) => roundSource(Number(value)))
        : [0, 0, 36];
      const canonicalMidpoint = Array.isArray(tunedPoint.canonical_midpoint) && tunedPoint.canonical_midpoint.length === 3
        ? tunedPoint.canonical_midpoint.map((value) => roundSource(Number(value)))
        : [...localPoint];
      merged.push({
        index: merged.length,
        name: String(tunedPoint.name || `custom_${String(merged.length - canonical.length + 1).padStart(2, "0")}`),
        bone: String(tunedPoint.bone || ""),
        requiredWeaponClass: String(tunedPoint.required_weapon_class || ""),
        useFixedHeadOrigin: Boolean(tunedPoint.use_fixed_head_origin),
        canonicalMidpoint,
        localPoint,
        custom: true
      });
    }
  }

  return merged;
}

function midpoint(a, b) {
  return [
    roundSource((Number(a[0]) + Number(b[0])) * 0.5),
    roundSource((Number(a[1]) + Number(b[1])) * 0.5),
    roundSource((Number(a[2]) + Number(b[2])) * 0.5)
  ];
}

async function loadModel() {
  const loader = new GLTFLoader();
  const gltf = await loader.loadAsync(MODEL_URL);
  modelMaterials = [];
  gltf.scene.traverse((child) => {
    if (!child.isMesh) {
      return;
    }
    child.frustumCulled = false;
    if (child.material) {
      child.material = Array.isArray(child.material)
        ? child.material.map((material) => material.clone())
        : child.material.clone();
      const materials = Array.isArray(child.material) ? child.material : [child.material];
      for (const material of materials) {
        material.side = THREE.FrontSide;
        modelMaterials.push(material);
      }
    }
  });
  applyModelOpacity();

  modelRoot.add(gltf.scene);
  gltf.scene.updateMatrixWorld(true);
  restBoneMatrices = captureBoneMatrices(gltf.scene);

  const clip = gltf.animations.find((animation) => animation.name === TARGET_ANIMATION)
    ?? gltf.animations[FALLBACK_ANIMATION_INDEX]
    ?? gltf.animations.find((animation) => animation.name.includes(TARGET_ANIMATION))
    ?? gltf.animations[0];

  if (clip) {
    mixer = new THREE.AnimationMixer(gltf.scene);
    const action = mixer.clipAction(clip);
    action.reset();
    action.play();
    mixer.setTime(0);
    action.paused = true;
    gltf.scene.updateMatrixWorld(true);
    posedBoneMatrices = captureBoneMatrices(gltf.scene);
    autoGlbMatch.disabled = !canApplyAutomaticGlbMatch();
    setStatus(`Pose: ${clip.name}`);
  }

  applyModelCalibration();
  frameModel();
}

function captureBoneMatrices(root) {
  const requiredBones = new Set(points.map((point) => point.bone).filter(Boolean));
  const matrices = new Map();

  root.traverse((node) => {
    if (!requiredBones.has(node.name)) {
      return;
    }

    matrices.set(node.name, node.matrixWorld.clone());
  });

  return matrices;
}

function canApplyAutomaticGlbMatch() {
  return points.some((point) => point.bone && restBoneMatrices.has(point.bone) && posedBoneMatrices.has(point.bone));
}

function applyAutomaticGlbMatch() {
  if (!canApplyAutomaticGlbMatch()) {
    setStatus("Automatic GLB Match unavailable: missing bone matrices");
    return;
  }

  let appliedCount = 0;
  for (const point of points) {
    if (!point.bone || !restBoneMatrices.has(point.bone) || !posedBoneMatrices.has(point.bone)) {
      continue;
    }

    const restMatrix = restBoneMatrices.get(point.bone);
    const posedMatrix = posedBoneMatrices.get(point.bone);
    const restInverse = restMatrix.clone().invert();

    const canonicalWorld = sourceToThree(point.canonicalMidpoint);
    const canonicalModelLocal = modelRoot.worldToLocal(canonicalWorld.clone());
    const boneLocalOffset = canonicalModelLocal.applyMatrix4(restInverse);
    const posedModelLocal = boneLocalOffset.applyMatrix4(posedMatrix);
    const posedWorld = modelRoot.localToWorld(posedModelLocal.clone());

    point.localPoint = threeToSource(posedWorld).map(roundSource);
    point.mesh.position.copy(sourceToThree(point.localPoint));
    appliedCount++;
  }

  updateSelectionPanel();
  updatePointStyles();
  updateLabelPositions();
  updateExportPreview();
  setStatus(`Applied Automatic GLB Match to ${appliedCount} points`);
  playSound("confirm");
}

function frameModel() {
  const box = new THREE.Box3().setFromObject(modelRoot);
  if (box.isEmpty()) {
    return;
  }

  const center = box.getCenter(new THREE.Vector3());
  const size = box.getSize(new THREE.Vector3());
  const maxSize = Math.max(size.x, size.y, size.z);
  const distance = maxSize * 1.85;
  camera.position.set(center.x + distance * 0.65, center.y + maxSize * 0.48, center.z + distance);
  orbit.target.copy(center);
  orbit.target.y += maxSize * 0.08;
  orbit.update();
}

function buildPointMeshes() {
  pointGroup.clear();
  labelLayer.innerHTML = "";

  for (const point of points) {
    createPointMesh(point);
  }
}

function createPointMesh(point) {
  const geometry = new THREE.SphereGeometry(0.0175, 24, 16);
  const material = new THREE.MeshBasicMaterial({
    color: new THREE.Color(0xffffff),
    depthTest: false,
    depthWrite: false,
    toneMapped: false
  });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.copy(sourceToThree(point.localPoint));
  mesh.renderOrder = 1000;
  mesh.userData.point = point;

  const label = document.createElement("div");
  label.className = "point-label";
  label.textContent = `${String(point.index).padStart(2, "0")} ${point.name}`;
  labelLayer.append(label);

  point.mesh = mesh;
  point.label = label;
  point.baseColor = new THREE.Color(0xffffff);
  pointGroup.add(mesh);
}

function populatePointSelect() {
  pointSelect.innerHTML = "";
  for (const point of points) {
    const option = document.createElement("option");
    option.value = String(point.index);
    option.textContent = `${String(point.index).padStart(2, "0")} ${point.name}${point.custom ? " *" : ""}`;
    pointSelect.append(option);
  }
}

function updatePointNameUi(point) {
  if (!point) {
    return;
  }

  const labelText = `${String(point.index).padStart(2, "0")} ${point.name}`;
  point.label.textContent = labelText;
  const option = pointSelect.querySelector(`option[value="${point.index}"]`);
  if (option) {
    option.textContent = `${labelText}${point.custom ? " *" : ""}`;
  }
}

function selectPoint(point) {
  if (!point) {
    selectedPoint = null;
    transformControls.detach();
    updatePointStyles();
    updateCustomPointControls();
    return;
  }

  selectedPoint = point;
  pointSelect.value = String(point.index);
  transformControls.attach(point.mesh);
  applyAxisLock();
  updateSelectionPanel();
  updatePointStyles();
  updateCustomPointControls();
  if (focusedPoint) {
    setFocusedPoint(point);
  }
}

function updateSelectionPanel() {
  if (!selectedPoint) {
    return;
  }

  coordX.value = formatInput(selectedPoint.localPoint[0]);
  coordY.value = formatInput(selectedPoint.localPoint[1]);
  coordZ.value = formatInput(selectedPoint.localPoint[2]);
  pointName.value = selectedPoint.name;
  pointName.disabled = !selectedPoint.custom;
  pointName.title = selectedPoint.custom ? "Rename custom point" : "Built-in visibility point names are locked";
  updateCustomPointControls();
}

function updateCustomPointControls() {
  deleteCustomPointButton.disabled = !selectedPoint?.custom;
  pointName.disabled = !selectedPoint?.custom;
  resetCustomButton.disabled = !points.some((point) => point.custom);
}

function renameSelectedPoint() {
  if (!selectedPoint?.custom) {
    pointName.value = selectedPoint?.name ?? "";
    return;
  }

  const nextName = pointName.value.trim().replace(/[\r\n\t]+/g, " ").replace(/\s{2,}/g, " ").slice(0, 48);
  if (!nextName) {
    pointName.value = selectedPoint.name;
    setStatus("Custom point name cannot be empty");
    return;
  }

  selectedPoint.name = nextName;
  pointName.value = nextName;
  updatePointNameUi(selectedPoint);
  updateExportPreview();
  setStatus(`Renamed ${nextPointLabel(selectedPoint)}`);
}

function nextPointLabel(point) {
  return `${String(point.index).padStart(2, "0")} ${point.name}`;
}

function setSelectedSourcePoint(values) {
  if (!selectedPoint) {
    return;
  }

  selectedPoint.localPoint = values.map((value) => roundSource(Number(value)));
  selectedPoint.mesh.position.copy(sourceToThree(selectedPoint.localPoint));
  updateFocusTarget();
  updateSelectionPanel();
  updateLabelPositions();
  updateExportPreview();
}

function syncPointFromMesh(point) {
  point.localPoint = threeToSource(point.mesh.position).map(roundSource);
}

function sourceToThree(point) {
  return new THREE.Vector3(
    Number(point[1]) / unitScale,
    Number(point[2]) / unitScale,
    -Number(point[0]) / unitScale
  );
}

function threeToSource(vector) {
  return [
    -vector.z * unitScale,
    vector.x * unitScale,
    vector.y * unitScale
  ];
}

function sourceAxisToThree(axis) {
  if (axis === "x") {
    return new THREE.Vector3(0, 0, -1);
  }
  if (axis === "y") {
    return new THREE.Vector3(1, 0, 0);
  }
  return new THREE.Vector3(0, 1, 0);
}

function roundSource(value) {
  return Number(Number(value).toFixed(3));
}

function formatInput(value) {
  return Number(value).toFixed(3).replace(/\.?0+$/, "");
}

function setStatus(message) {
  statusEl.textContent = message;
}

function playSound(kind = "tap") {
  try {
    audioContext ??= new (window.AudioContext || window.webkitAudioContext)();
    if (audioContext.state === "suspended") {
      audioContext.resume();
    }

    const now = audioContext.currentTime;
    const gain = audioContext.createGain();
    const oscillator = audioContext.createOscillator();
    const settings = {
      tap: [420, 0.035, 0.018],
      confirm: [620, 0.055, 0.024],
      reset: [280, 0.07, 0.022],
      delete: [180, 0.06, 0.018],
      focus: [520, 0.045, 0.018]
    }[kind] ?? [420, 0.035, 0.018];

    oscillator.type = "sine";
    oscillator.frequency.setValueAtTime(settings[0], now);
    gain.gain.setValueAtTime(0.0001, now);
    gain.gain.exponentialRampToValueAtTime(settings[2], now + 0.008);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + settings[1]);
    oscillator.connect(gain);
    gain.connect(audioContext.destination);
    oscillator.start(now);
    oscillator.stop(now + settings[1] + 0.01);
  } catch {
    // Sound is optional; browser audio policy failures should not affect editing.
  }
}

function applyAxisLock() {
  for (const button of document.querySelectorAll("[data-axis]")) {
    button.classList.toggle("active", button.dataset.axis === axisLock);
  }

  transformControls.showX = axisLock === "free" || axisLock === "y";
  transformControls.showY = axisLock === "free" || axisLock === "z";
  transformControls.showZ = axisLock === "free" || axisLock === "x";
}

function updatePointStyles() {
  for (const point of points) {
    const selected = point === selectedPoint;
    const hovered = point === hoveredPoint;
    point.mesh.scale.setScalar(selected ? 1.28 : hovered ? 1.12 : 1);
    point.mesh.material.color.set(0xffffff);
    point.label.classList.toggle("visible", selected || hovered);
  }
}

function updateLabelPositions() {
  const width = window.innerWidth;
  const height = window.innerHeight;

  for (const point of points) {
    const projected = point.mesh.position.clone().project(camera);
    const visible = projected.z > -1 && projected.z < 1;
    point.label.style.display = visible ? "block" : "none";
    point.label.style.left = `${(projected.x * 0.5 + 0.5) * width}px`;
    point.label.style.top = `${(-projected.y * 0.5 + 0.5) * height}px`;
  }
}

function updatePointer(event) {
  const rect = renderer.domElement.getBoundingClientRect();
  pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
  pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
}

function pickPoint(event) {
  updatePointer(event);
  raycaster.setFromCamera(pointer, camera);
  const hits = raycaster.intersectObjects(points.map((point) => point.mesh), false);
  return hits.length ? hits[0].object.userData.point : null;
}

function startPointDrag(event, point) {
  selectPoint(point);
  updatePointer(event);
  raycaster.setFromCamera(pointer, camera);

  const pointPosition = point.mesh.position.clone();
  const cameraDirection = camera.getWorldDirection(new THREE.Vector3()).normalize();
  let planeNormal = cameraDirection.clone();
  let axis = null;

  if (axisLock !== "free") {
    axis = sourceAxisToThree(axisLock).normalize();
    planeNormal = cameraDirection.clone().cross(axis).cross(axis).normalize();
    if (planeNormal.lengthSq() < 0.0001) {
      planeNormal = new THREE.Vector3(0, 1, 0).cross(axis).cross(axis).normalize();
    }
  }

  const plane = new THREE.Plane().setFromNormalAndCoplanarPoint(planeNormal, pointPosition);
  if (!raycaster.ray.intersectPlane(plane, dragIntersection)) {
    return;
  }

  dragState = {
    point,
    axis,
    plane,
    startPointerWorld: dragIntersection.clone(),
    startMeshWorld: pointPosition
  };
  orbit.enabled = false;
  transformControls.enabled = false;
  renderer.domElement.setPointerCapture(event.pointerId);
}

function dragPoint(event) {
  if (!dragState) {
    return;
  }

  updatePointer(event);
  raycaster.setFromCamera(pointer, camera);
  if (!raycaster.ray.intersectPlane(dragState.plane, dragIntersection)) {
    return;
  }

  const fineScale = fineMode.checked ? 0.22 : 1;
  const delta = dragIntersection.clone().sub(dragState.startPointerWorld).multiplyScalar(fineScale);
  let nextPosition = dragState.startMeshWorld.clone();

  if (dragState.axis) {
    const amount = delta.dot(dragState.axis);
    nextPosition.add(dragState.axis.clone().multiplyScalar(amount));
  } else {
    nextPosition.add(delta);
  }

  dragState.point.mesh.position.copy(nextPosition);
  syncPointFromMesh(dragState.point);
  updateFocusTarget();
  updateSelectionPanel();
  updateLabelPositions();
  updateExportPreview();
}

function endPointDrag(event) {
  if (!dragState) {
    return;
  }

  renderer.domElement.releasePointerCapture(event.pointerId);
  dragState = null;
  transformControls.enabled = true;
  orbit.enabled = orbitEnabled.checked;
}

function resetSelectedPoint() {
  if (!selectedPoint) {
    return;
  }
  setSelectedSourcePoint(selectedPoint.canonicalMidpoint);
  playSound("reset");
}

function resetAllPoints() {
  for (const point of points) {
    point.localPoint = [...point.canonicalMidpoint];
    point.mesh.position.copy(sourceToThree(point.localPoint));
  }
  updateFocusTarget();
  updateSelectionPanel();
  updateLabelPositions();
  updateExportPreview();
  playSound("reset");
  setStatus("Reset all points");
}

function resetCustomPoints() {
  let resetCount = 0;
  for (const point of points) {
    if (!point.custom) {
      continue;
    }

    point.localPoint = [...point.canonicalMidpoint];
    point.mesh.position.copy(sourceToThree(point.localPoint));
    resetCount++;
  }

  updateFocusTarget();
  updateSelectionPanel();
  updateLabelPositions();
  updateExportPreview();
  playSound("reset");
  setStatus(`Reset ${resetCount} custom dots`);
}

function addCustomPoint() {
  const sourcePoint = selectedPoint
    ? [selectedPoint.localPoint[0], selectedPoint.localPoint[1] + 2, selectedPoint.localPoint[2]]
    : threeToSource(orbit.target).map(roundSource);
  const customNumber = points.filter((point) => point.custom).length + 1;
  const point = {
    index: points.length,
    name: `custom_${String(customNumber).padStart(2, "0")}`,
    bone: "",
    requiredWeaponClass: "",
    useFixedHeadOrigin: false,
    canonicalMidpoint: sourcePoint.map(roundSource),
    localPoint: sourcePoint.map(roundSource),
    custom: true
  };

  points.push(point);
  createPointMesh(point);
  populatePointSelect();
  selectPoint(point);
  updateLabelPositions();
  updateExportPreview();
  autoGlbMatch.disabled = !canApplyAutomaticGlbMatch();
  resetCustomButton.disabled = false;
  playSound("confirm");
  setStatus(`Added ${point.name}`);
}

function deleteSelectedCustomPoint() {
  if (!selectedPoint?.custom) {
    return;
  }

  const deleteIndex = selectedPoint.index;
  pointGroup.remove(selectedPoint.mesh);
  selectedPoint.mesh.geometry.dispose();
  selectedPoint.mesh.material.dispose();
  selectedPoint.label.remove();
  points.splice(deleteIndex, 1);
  if (focusedPoint === selectedPoint) {
    clearFocusPoint();
  }
  renumberPoints();
  populatePointSelect();
  selectPoint(points[Math.min(deleteIndex, points.length - 1)]);
  updateLabelPositions();
  updateExportPreview();
  updateCustomPointControls();
  playSound("delete");
  setStatus("Deleted custom dot");
}

function renumberPoints() {
  points.forEach((point, index) => {
    point.index = index;
    point.mesh.userData.point = point;
    point.mesh.material.color.set(0xffffff);
    point.baseColor = new THREE.Color(0xffffff);
    updatePointNameUi(point);
  });
}

function toggleFocusSelectedPoint() {
  if (focusedPoint) {
    clearFocusPoint();
    return;
  }

  if (!selectedPoint) {
    return;
  }

  setFocusedPoint(selectedPoint);
  playSound("focus");
  setStatus(`Focused ${selectedPoint.name}`);
}

function setFocusedPoint(point) {
  if (!focusedPoint) {
    preFocusCameraState = {
      position: camera.position.clone(),
      target: orbit.target.clone()
    };
  }

  focusedPoint = point;
  focusSelectedButton.textContent = "Unfocus";
  focusSelectedButton.title = "Return to normal orbit";
  updateFocusTarget(true);
}

function clearFocusPoint() {
  focusedPoint = null;
  focusSelectedButton.textContent = "Focus";
  focusSelectedButton.title = "Center the camera on the selected dot";
  if (preFocusCameraState) {
    camera.position.copy(preFocusCameraState.position);
    orbit.target.copy(preFocusCameraState.target);
    orbit.update();
    preFocusCameraState = null;
  }
  playSound("focus");
  setStatus("Unfocused");
}

function updateFocusTarget(repositionCamera = false) {
  if (!focusedPoint) {
    return;
  }

  const target = focusedPoint.mesh.position.clone();
  const offset = camera.position.clone().sub(orbit.target);
  orbit.target.copy(target);
  if (repositionCamera) {
    const distance = THREE.MathUtils.clamp(offset.length(), 0.75, 2.4);
    offset.setLength(distance);
    camera.position.copy(target).add(offset);
  }
  orbit.update();
}

function rebuildPointPositionsForScale() {
  for (const point of points) {
    point.mesh.position.copy(sourceToThree(point.localPoint));
  }
  updateLabelPositions();
}

function applyModelCalibration() {
  const yaw = Number(document.querySelector("#modelYaw").value || 0);
  const modelScale = Number(document.querySelector("#modelScale").value || 1);
  const offsetX = Number(document.querySelector("#modelOffsetX").value || 0);
  const offsetY = Number(document.querySelector("#modelOffsetY").value || 0);
  const offsetZ = Number(document.querySelector("#modelOffsetZ").value || 0);

  modelRoot.rotation.set(0, THREE.MathUtils.degToRad(yaw), 0);
  modelRoot.scale.setScalar(modelScale);
  modelRoot.position.set(offsetX, offsetY, offsetZ);
}

function applyModelOpacity() {
  const opacity = Number(modelOpacity.value || 1);
  for (const material of modelMaterials) {
    material.opacity = opacity;
    material.transparent = opacity < 0.999;
    material.depthWrite = opacity > 0.999;
    material.needsUpdate = true;
  }
  pointGroup.traverse((node) => {
    if (!node.isMesh) {
      return;
    }
    node.renderOrder = 1000;
    node.material.depthTest = false;
    node.material.depthWrite = false;
    node.material.color.set(0xffffff);
  });
  setStatus(`Model opacity ${Math.round(opacity * 100)}%`);
}

function exportJsonObject() {
  return {
    source: {
      model: "ct sas/source/sas blue.glb",
      animation: TARGET_ANIMATION,
      baseline: "tools/cs2_player_hitboxes_canonical.json",
      coordinate_space: "source_local_units",
      description: "Editable visibility points for the CT SAS blue model in the tools_preview_sas blue pose."
    },
    viewer_mapping: {
      source_x: "forward/back",
      source_y: "left/right",
      source_z: "up",
      three_x: "source_y / unit_scale",
      three_y: "source_z / unit_scale",
      three_z: "-source_x / unit_scale",
      unit_scale: unitScale
    },
    primitive_count: points.length,
    points: points.map((point) => ({
      index: point.index,
      name: point.name,
      bone: point.bone,
      custom: point.custom,
      required_weapon_class: getRequiredWeaponClass(point),
      use_fixed_head_origin: point.useFixedHeadOrigin,
      local_point: point.localPoint.map(roundSource),
      canonical_midpoint: point.canonicalMidpoint.map(roundSource)
    }))
  };
}

function getRequiredWeaponClass(point) {
  if (point.requiredWeaponClass) {
    return point.requiredWeaponClass;
  }

  const name = point.name.trim().toLowerCase();
  if (name === "pistol") {
    return "Pistol";
  }
  if (name === "rifle") {
    return "Rifle";
  }
  if (name === "awp" || name === "sniper") {
    return "Sniper";
  }
  return "None";
}

function generateJsonExport() {
  return `${JSON.stringify(exportJsonObject(), null, 2)}\n`;
}

function csharpFloat(value) {
  let text = Number(value).toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
  if (text === "-0") {
    text = "0";
  }
  if (!text.includes(".")) {
    text += ".0";
  }
  return `${text}f`;
}

function generateCsharpExport() {
  const lines = [
    "using System.Numerics;",
    "using S2FOW.Models;",
    "",
    "namespace S2FOW.Core;",
    "",
    "internal readonly struct VisibilityPrimitive",
    "{",
    "    public required Vector3 LocalPoint { get; init; }",
    "    public bool UseFixedHeadOrigin { get; init; }",
    "    public WeaponLosClass RequiredWeaponClass { get; init; }",
    "}",
    "",
    "internal static class Cs2VisibilityPrimitiveLayout",
    "{",
    `    public const int PrimitiveCount = ${points.length};`,
    "    public const int AabbPointCount = 8;",
    "    public const int MaxVisibilityTestPoints = PrimitiveCount + AabbPointCount;",
    "",
    "    private static readonly VisibilityPrimitive[] _primitives =",
    "    ["
  ];

  points.forEach((point, index) => {
    const [x, y, z] = point.localPoint;
    const comma = index < points.length - 1 ? "," : "";
    const extraProperties = [];
    if (point.useFixedHeadOrigin) {
      extraProperties.push("            UseFixedHeadOrigin = true");
    }
    const requiredWeaponClass = getRequiredWeaponClass(point);
    if (requiredWeaponClass !== "None") {
      extraProperties.push(`            RequiredWeaponClass = WeaponLosClass.${requiredWeaponClass}`);
    }
    lines.push("        new()");
    lines.push("        {");
    lines.push(
      `            LocalPoint = new Vector3(${csharpFloat(x)}, ${csharpFloat(y)}, ${csharpFloat(z)})${extraProperties.length ? "," : ""}`
    );
    for (let i = 0; i < extraProperties.length; i++) {
      lines.push(`${extraProperties[i]}${i < extraProperties.length - 1 ? "," : ""}`);
    }
    lines.push(`        }${comma}`);
  });

  lines.push("    ];");
  lines.push("");
  lines.push("    public static ReadOnlySpan<VisibilityPrimitive> Primitives => _primitives;");
  lines.push("}");
  lines.push("");
  return lines.join("\n");
}

function updateExportPreview() {
  exportText.value = exportKind.value === "json" ? generateJsonExport() : generateCsharpExport();
}

async function copyExport() {
  updateExportPreview();
  try {
    await navigator.clipboard.writeText(exportText.value);
    setStatus(`Copied ${exportKind.value === "json" ? "JSON" : "C#"} export`);
    playSound("confirm");
  } catch {
    exportText.select();
    document.execCommand("copy");
    setStatus("Copied using selection fallback");
    playSound("confirm");
  }
}

function downloadJson() {
  const blob = new Blob([generateJsonExport()], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = "sas_blue_tools_preview_los_points.json";
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
  setStatus("Downloaded JSON");
  playSound("confirm");
}

function loadJsonFile(file) {
  const reader = new FileReader();
  reader.onload = () => {
    try {
      const data = JSON.parse(String(reader.result));
      if (!Array.isArray(data.points) || data.points.length < canonicalPoints.length) {
        throw new Error(`JSON must contain at least ${canonicalPoints.length} points.`);
      }

      points = buildPointsFromLoadedJson(data);
      transformControls.detach();
      buildPointMeshes();
      populatePointSelect();
      selectPoint(points[0]);

      updateLabelPositions();
      updateExportPreview();
      autoGlbMatch.disabled = !canApplyAutomaticGlbMatch();
      setStatus(`Loaded ${file.name}`);
      playSound("confirm");
    } catch (error) {
      setStatus(error.message);
      playSound("delete");
    }
  };
  reader.readAsText(file);
}

function buildPointsFromLoadedJson(data) {
  return data.points
    .slice()
    .sort((a, b) => Number(a.index) - Number(b.index))
    .map((loadedPoint, index) => {
      if (!Array.isArray(loadedPoint.local_point) || loadedPoint.local_point.length !== 3) {
        throw new Error(`Invalid point at index ${loadedPoint.index}.`);
      }

      const base = canonicalPoints[index];
      const localPoint = loadedPoint.local_point.map((value) => roundSource(Number(value)));
      const canonicalMidpoint = Array.isArray(loadedPoint.canonical_midpoint) && loadedPoint.canonical_midpoint.length === 3
        ? loadedPoint.canonical_midpoint.map((value) => roundSource(Number(value)))
        : base?.canonicalMidpoint ? [...base.canonicalMidpoint] : [...localPoint];

      return {
        index,
        name: String(loadedPoint.name || base?.name || `custom_${String(index - canonicalPoints.length + 1).padStart(2, "0")}`),
        bone: String(loadedPoint.bone || base?.bone || ""),
        requiredWeaponClass: String(loadedPoint.required_weapon_class || ""),
        useFixedHeadOrigin: Boolean(loadedPoint.use_fixed_head_origin ?? base?.useFixedHeadOrigin),
        canonicalMidpoint,
        localPoint,
        custom: Boolean(loadedPoint.custom || index >= canonicalPoints.length || !base)
      };
    });
}

function animate() {
  requestAnimationFrame(animate);
  if (mixer) {
    mixer.update(0);
  }
  orbit.update();
  updateLabelPositions();
  renderer.render(scene, camera);
}

function startAnimationLoop() {
  if (animationStarted) {
    return;
  }

  animationStarted = true;
  animate();
}

window.addEventListener("resize", () => {
  camera.aspect = window.innerWidth / window.innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);
  updateLabelPositions();
});

renderer.domElement.addEventListener("pointerdown", (event) => {
  if (event.button !== 0) {
    return;
  }

  const point = pickPoint(event);
  if (!point) {
    return;
  }

  event.preventDefault();
  event.stopImmediatePropagation();
  startPointDrag(event, point);
}, true);

renderer.domElement.addEventListener("pointermove", (event) => {
  if (dragState) {
    dragPoint(event);
    return;
  }

  const point = pickPoint(event);
  if (point !== hoveredPoint) {
    hoveredPoint = point;
    updatePointStyles();
  }
});

renderer.domElement.addEventListener("pointerup", endPointDrag);
renderer.domElement.addEventListener("pointercancel", endPointDrag);
renderer.domElement.addEventListener("pointerleave", () => {
  if (!dragState) {
    hoveredPoint = null;
    updatePointStyles();
  }
});

renderer.domElement.addEventListener("wheel", (event) => {
  if (!orbitEnabled.checked) {
    return;
  }

  if (event.defaultPrevented) {
    return;
  }

  event.preventDefault();
  const direction = camera.position.clone().sub(orbit.target);
  const distance = direction.length();
  const zoomFactor = Math.exp(Math.sign(event.deltaY) * 0.18);
  const nextDistance = THREE.MathUtils.clamp(distance * zoomFactor, orbit.minDistance, orbit.maxDistance);
  direction.setLength(nextDistance);
  camera.position.copy(orbit.target).add(direction);
  orbit.update();
}, { passive: false });

pointSelect.addEventListener("change", () => {
  selectPoint(points[Number(pointSelect.value)]);
  playSound("tap");
});

pointName.addEventListener("change", () => {
  renameSelectedPoint();
  playSound("tap");
});

pointName.addEventListener("keydown", (event) => {
  if (event.key === "Enter") {
    event.preventDefault();
    renameSelectedPoint();
    pointName.blur();
  }
});

for (const input of [coordX, coordY, coordZ]) {
  input.addEventListener("input", () => {
    if (!selectedPoint) {
      return;
    }
    setSelectedSourcePoint([
      Number(coordX.value),
      Number(coordY.value),
      Number(coordZ.value)
    ]);
  });
}

for (const button of document.querySelectorAll("[data-axis]")) {
  button.addEventListener("click", () => {
    axisLock = button.dataset.axis;
    applyAxisLock();
    playSound("tap");
  });
}

orbitEnabled.addEventListener("change", () => {
  orbit.enabled = orbitEnabled.checked;
  playSound("tap");
});

document.querySelector("#resetSelected").addEventListener("click", resetSelectedPoint);
document.querySelector("#resetAll").addEventListener("click", resetAllPoints);
resetCustomButton.addEventListener("click", resetCustomPoints);
focusSelectedButton.addEventListener("click", toggleFocusSelectedPoint);
autoGlbMatch.addEventListener("click", applyAutomaticGlbMatch);
addCustomPointButton.addEventListener("click", addCustomPoint);
deleteCustomPointButton.addEventListener("click", deleteSelectedCustomPoint);
document.querySelector("#toggleExport").addEventListener("click", () => {
  exportDrawer.classList.toggle("hidden");
  updateExportPreview();
  playSound("tap");
});
document.querySelector("#copyExport").addEventListener("click", copyExport);
document.querySelector("#downloadJson").addEventListener("click", downloadJson);
exportKind.addEventListener("change", updateExportPreview);
document.querySelector("#loadJson").addEventListener("change", (event) => {
  const [file] = event.target.files;
  if (file) {
    loadJsonFile(file);
  }
  event.target.value = "";
});

document.querySelector("#toggleCalibration").addEventListener("click", () => {
  document.querySelector("#calibrationBody").classList.toggle("hidden");
  playSound("tap");
});

for (const id of ["modelYaw", "modelScale", "modelOffsetX", "modelOffsetY", "modelOffsetZ"]) {
  document.querySelector(`#${id}`).addEventListener("input", () => {
    applyModelCalibration();
    updateLabelPositions();
  });
}

modelOpacity.addEventListener("input", applyModelOpacity);

document.querySelector("#unitScale").addEventListener("input", (event) => {
  unitScale = Math.max(1, Number(event.target.value) || DEFAULT_UNIT_SCALE);
  rebuildPointPositionsForScale();
  updateExportPreview();
});
