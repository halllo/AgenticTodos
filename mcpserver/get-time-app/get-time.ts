import { App } from "@modelcontextprotocol/ext-apps";

const serverTimeEl = document.getElementById("server-time")!;
const getTimeBtn = document.getElementById("get-time-btn")!;
const setSizeBtn = document.getElementById("set-size-btn")!;
const resetSizeBtn = document.getElementById("reset-size-btn")!;

const app = new App({ name: "Get Time App", version: "1.0.0" }, {}, { autoResize: false });

// Establish communication with the host
app.connect();

// Handle the initial tool result pushed by the host
app.ontoolresult = (result) => {
  const time = result.content?.find((c) => c.type === "text")?.text;
  serverTimeEl.textContent = time ?? "[ERROR]";
};

// Proactively call tools when users interact with the UI
getTimeBtn.addEventListener("click", async () => {
  const result = await app.callServerTool({
    name: "get_time",
    arguments: {},
  });
  const time = result.content?.find((c) => c.type === "text")?.text;
  serverTimeEl.textContent = time ?? "[ERROR]";
});

setSizeBtn.addEventListener("click", () => {
  const el = document.documentElement;
  el.style.width = "600px";
  el.style.height = "600px";
  app.sendSizeChanged({ width: 600, height: 600 });
});

resetSizeBtn.addEventListener("click", () => {
  const el = document.documentElement;
  el.style.width = "max-content";
  el.style.height = "max-content";
  const { width, height } = el.getBoundingClientRect();
  el.style.removeProperty("width");
  el.style.removeProperty("height");
  app.sendSizeChanged({ width: Math.ceil(width), height: Math.ceil(height) });
});