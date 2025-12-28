# Copilot instructions

This repository is set up to use Aspire. Aspire is an orchestrator for the entire application and will take care of configuring dependencies, building, and running the application. The resources that make up the application are defined in `apphost.cs` including application code and external dependencies.

After you made changes, run the application using `aspire run`. When the application is running, access the frontend at <https://gateway-aspireexperiments.dev.localhost:8443/frontend/>.

(You can stop the application using `pkill -INT -f "aspire run"`.)
