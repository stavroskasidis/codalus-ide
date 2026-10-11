# RPM spec for the prebuilt app (filled in by eng/package.sh; M31, ADR 0028). It packages the published folder as it
# is: no build step, no stripping (the shell is a Native AOT binary, the hosts are .NET assemblies).
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none
%global __requires_exclude_from ^/opt/daedalus-studio/.*$
%global __provides_exclude_from ^/opt/daedalus-studio/.*$

Name:           daedalus-studio
Version:        {{VERSION}}
Release:        1
Summary:        Cross-platform, open-source IDE for .NET
License:        MIT
URL:            https://github.com/stavroskasidis/daedalus-studio
AutoReqProv:    no
Requires:       git
Requires:       libicu
Requires:       fontconfig
Recommends:     dotnet-sdk-10.0

%description
Daedalus Studio is an MIT-licensed .NET IDE with solutions, C# language
services, build and run, debugging on netcoredbg, and Git. It needs the
.NET SDK to open and build solutions.

%install
mkdir -p %{buildroot}/opt %{buildroot}%{_bindir} %{buildroot}%{_datadir}/applications %{buildroot}%{_datadir}/icons/hicolor/256x256/apps
cp -a {{STAGE}} %{buildroot}/opt/daedalus-studio
ln -s /opt/daedalus-studio/daedalus %{buildroot}%{_bindir}/daedalus
cp {{REPO}}/eng/packaging/linux/daedalus-studio.desktop %{buildroot}%{_datadir}/applications/
cp {{REPO}}/assets/logo/png/daedalus-256.png %{buildroot}%{_datadir}/icons/hicolor/256x256/apps/daedalus-studio.png

%files
/opt/daedalus-studio
%{_bindir}/daedalus
%{_datadir}/applications/daedalus-studio.desktop
%{_datadir}/icons/hicolor/256x256/apps/daedalus-studio.png
