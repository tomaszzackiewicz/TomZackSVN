# TomZackSVN
Light SVN Client
TomZackSVN was created because there are currently no modern, free SVN clients that properly support the svn+ssh protocol. Most existing tools are outdated, limited, or require paid licenses. This project fills that gap by providing a simple and accessible solution for everyone.

The application relies on the TortoiseSVN command‑line interface (CLI), so TortoiseSVN must be installed on the system. Additionally, OpenSSH needs to be added to the system’s environment variables, and users must generate SSH keys for authentication in order for the client to work correctly.

You only need to download the project, open it in Unity, and build the Windows executable to start using the client.


Installation Checklist for Using the Application:

- Install TortoiseSVN (make sure to select Command Line Tools) or SlikSVN (which includes only CLI tools for Windows). 
- Verify that the OpenSSH Client is enabled in Windows Features and that its path is included in the Windows environment variables.
- Generate SSH keys (the application requires a private key for authentication). 
- Ensure you have write permissions for the folder you choose as your Working Directory. 


Third-Party Libraries

This project utilizes the following open-source library to handle native file and folder dialogues:

    Unity Standalone File Browser

        Author: Gökhan Gökçe (gkngkc)

        License: MIT

        Description: Used for providing native Windows/Mac/Linux file selection interfaces within the SVN Client.
