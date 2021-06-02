import App from "./App";
import { BrowserRouter } from "react-router-dom";
import React from "react";
import ReactDOM from "react-dom";
import { initializeIcons } from '@fluentui/react/lib/Icons';
const baseUrl = document.getElementsByTagName("base")[0].getAttribute("href");
const rootElement = document.getElementById("root");

initializeIcons();

ReactDOM.render(
  <BrowserRouter basename={baseUrl === null ? "BrokenUrl" : baseUrl}>
    <App />
  </BrowserRouter>,
  rootElement
);
