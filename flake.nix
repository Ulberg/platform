# The tools for working on the platform (D79): the same for developers (`nix develop`) and for Claude's sandbox
# in the platform profile (D77), which is built from this.
{
  description = "platform: developer tools";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
  outputs = { nixpkgs, ... }:
    let
      systems = [ "aarch64-darwin" "aarch64-linux" "x86_64-linux" ];
      forEach = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      # The sandbox's image (platform profile) adds only what Docker's Claude sandbox image lacks (it has Docker, git,
      # gh, make, jq). Not az: Azure is reached through a proxy on the developer's machine (D78).
      packages = forEach (pkgs: {
        tools = pkgs.buildEnv {
          name = "platform-sandbox-tools";
          paths = with pkgs; [ cosign kind kubectl kubernetes-helm yq-go pulumi ]
            ++ pkgs.lib.optionals pkgs.stdenv.hostPlatform.isLinux [ pkgs.dotnetCorePackages.sdk_11_0 ];
        };
      });
      devShells = forEach (pkgs: {
        default = pkgs.mkShell {
          packages = with pkgs; [
            pulumi # infrastructure (applied by CI only)
            azure-cli # read-only as the agent identity (D41)
            docker-client
            kind
            kubectl
            kubernetes-helm
            cosign
            gnumake
            git
            gh
            jq
            yq-go
          ]
          # .NET from nixpkgs on Linux (the sandbox, CI-like machines); on macOS it would build Swift from source,
          # so there the system's .NET 11 is used (Homebrew).
          ++ pkgs.lib.optionals pkgs.stdenv.hostPlatform.isLinux [ pkgs.dotnetCorePackages.sdk_11_0 ];
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
        };
      });
    };
}
