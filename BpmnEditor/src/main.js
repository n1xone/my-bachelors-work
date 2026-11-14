import Modeler from 'bpmn-js/lib/Modeler';
import 'bpmn-js/dist/assets/diagram-js.css';
import 'bpmn-js/dist/assets/bpmn-font/css/bpmn.css';
import 'bpmn-js/dist/assets/bpmn-font/css/bpmn-codes.css';
import './style.css';

const modeler = new Modeler({ container: '#canvas' });
const saveButton = document.querySelector('#save');
const saveState = document.querySelector('#save-state');
const fileName = document.querySelector('#file-name');
const modelPicker = document.querySelector('#model-picker');
const errorPanel = document.querySelector('#error-panel');
const selectionState = document.querySelector('#selection-state');
let dirty = false;
let loading = true;
let currentModelPath = null;

function setDirty(value) {
  dirty = value;
  saveButton.disabled = !dirty;
  saveState.textContent = dirty ? 'Unsaved changes' : 'All changes saved';
  saveState.classList.toggle('is-dirty', dirty);
}

async function populateModelPicker() {
  const response = await fetch('/api/models');
  if (!response.ok) throw new Error(await response.text());

  const models = await response.json();
  modelPicker.replaceChildren(...models.map((model) => new Option(model.label, model.value)));
  if (currentModelPath) modelPicker.value = currentModelPath;
}

async function loadModel(relativePath = currentModelPath) {
  const query = relativePath ? `?path=${encodeURIComponent(relativePath)}` : '';
  const response = await fetch(`/api/model${query}`);
  if (!response.ok) throw new Error(await response.text());
  const model = await response.json();
  currentModelPath = model.relativePath;
  fileName.textContent = model.engineAddress ? `${model.fileName} → ${model.engineAddress}` : model.fileName;
  document.title = `${model.fileName} | BPMN Studio`;
  modelPicker.value = model.relativePath;
  await modeler.importXML(model.xml);
  modeler.get('canvas').zoom('fit-viewport');
  errorPanel.hidden = true;
  loading = false;
  setDirty(false);
}

void (async function initialize() {
  try {
    await populateModelPicker();
    await loadModel();
  } catch (error) {
    errorPanel.hidden = false;
    errorPanel.textContent = `Could not open BPMN: ${error.message}`;
    saveState.textContent = 'Load failed';
  }
})();

modelPicker.addEventListener('change', async () => {
  if (!modelPicker.value) return;
  if (dirty && !window.confirm('Discard unsaved changes and open another model?')) {
    modelPicker.value = currentModelPath;
    return;
  }
  loading = true;
  try {
    await loadModel(modelPicker.value);
  } catch (error) {
    loading = false;
    modelPicker.value = currentModelPath;
    errorPanel.hidden = false;
    errorPanel.textContent = `Could not open BPMN: ${error.message}`;
    saveState.textContent = 'Load failed';
  }
});

modeler.on('commandStack.changed', () => {
  if (!loading) setDirty(true);
});

modeler.on('selection.changed', ({ newSelection }) => {
  selectionState.textContent = newSelection.length
    ? `${newSelection.length} element(s) selected`
    : 'Select an element to edit the diagram';
});

saveButton.addEventListener('click', async () => {
  saveButton.disabled = true;
  saveState.textContent = 'Updating Camunda… check terminal for input';
  saveState.title = 'При запросе данных заполните их в терминале. Экземпляр продолжит работу с тем же ID.';
  try {
    const { xml } = await modeler.saveXML({ format: true });
    const url = currentModelPath ? `/api/model?path=${encodeURIComponent(currentModelPath)}` : '/api/model';
    const response = await fetch(url, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/xml; charset=utf-8' },
      body: xml
    });
    if (!response.ok) throw new Error(await response.text());
    const result = await response.text();
    setDirty(false);
    saveState.textContent = result;
    saveState.title = result;
  } catch (error) {
    saveState.textContent = `Save failed: ${error.message}`;
    saveState.title = error.message;
    window.alert(`Не удалось обновить Camunda.\n${error.message}`);
    saveButton.disabled = false;
  }
});

const canvas = modeler.get('canvas');
document.querySelector('#zoom-in').addEventListener('click', () => canvas.zoom(canvas.zoom() + 0.2));
document.querySelector('#zoom-out').addEventListener('click', () => canvas.zoom(Math.max(0.2, canvas.zoom() - 0.2)));
document.querySelector('#zoom-fit').addEventListener('click', () => canvas.zoom('fit-viewport'));

window.addEventListener('beforeunload', (event) => {
  if (dirty) {
    event.preventDefault();
    event.returnValue = '';
  }
});