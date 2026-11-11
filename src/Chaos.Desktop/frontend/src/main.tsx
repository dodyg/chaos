import React from "react";
import { createRoot } from "react-dom/client";
import "./style.css";
import { App } from "./App";
import { applyTheme, loadActiveTheme } from "./appearance";

applyTheme(loadActiveTheme());

createRoot(document.querySelector("#app")!).render(<App />);
