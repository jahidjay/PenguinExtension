import "./style.css";
import { api } from "./bridge";
import { DesktopController } from "./controller";
import { mount } from "./view";
const controller = new DesktopController(api);
mount(document.querySelector<HTMLDivElement>("#app")!, controller);
void controller.initialize();
