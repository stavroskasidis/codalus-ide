# RPM spec for the prebuilt app (filled in by eng/package.sh; M31, ADR 0028). It packages the published folder as it
# is: no build step, no stripping (the shell is a Native AOT binary, the hosts are .NET assemblies).
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none
%global __requires_exclude_from ^/opt/codalus/.*$
%global __provides_exclude_from ^/opt/codalus/.*$

Name:           codalus
Version:        {{VERSION}}
Release:        1
Summary:        Cross-platform, open-source IDE for .NET
License:        MIT
URL:            https://github.com/stavroskasidis/codalus-ide
AutoReqProv:    no
Requires:       git
Requires:       libicu
Requires:       fontconfig
Recommends:     dotnet-sdk-11.0

%description
Codalus IDE is an MIT-licensed .NET IDE with solutions, C# language
services, build and run, debugging on netcoredbg, and Git. It needs the
.NET SDK to open and build solutions, and .NET 11 or later for its hosts.

%install
mkdir -p %{buildroot}/opt %{buildroot}%{_bindir} %{buildroot}%{_datadir}/applications %{buildroot}%{_datadir}/icons/hicolor/256x256/apps
cp -a {{STAGE}} %{buildroot}/opt/codalus
ln -s /opt/codalus/codalus %{buildroot}%{_bindir}/codalus
cp {{REPO}}/eng/packaging/linux/codalus.desktop %{buildroot}%{_datadir}/applications/
cp {{REPO}}/assets/logo/png/codalus-256.png %{buildroot}%{_datadir}/icons/hicolor/256x256/apps/codalus.png

%files
/opt/codalus
%{_bindir}/codalus
%{_datadir}/applications/codalus.desktop
%{_datadir}/icons/hicolor/256x256/apps/codalus.png
