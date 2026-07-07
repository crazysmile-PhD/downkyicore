# DownKyi Core Cross-Platform Edition

<div align="center">

[简体中文](README.md) | [繁體中文](README.zh-TW.md) | [English](README.en.md)

[![GitHub Repo stars](https://img.shields.io/github/stars/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/stargazers)
[![GitHub forks](https://img.shields.io/github/forks/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/network)
[![GitHub issues](https://img.shields.io/github/issues/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/issues)
[![LICENSE](https://img.shields.io/github/license/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/blob/main/LICENSE)

</div>

## Download

[![GitHub release (latest by date)](https://img.shields.io/github/v/release/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)
[![GitHub Release Date](https://img.shields.io/github/release-date/crazysmile-PhD/downkyicore)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)
[![GitHub all releases](https://img.shields.io/github/downloads/crazysmile-PhD/downkyicore/total)](https://github.com/crazysmile-PhD/downkyicore/releases/latest)

[Changelog](CHANGELOG.md)

## Introduction

- This project is a cross-platform edition based on [DownKyi for Windows](https://github.com/leiurayer/downkyi) and [Avalonia UI](https://github.com/AvaloniaUI/Avalonia). It supports Windows, Linux, and macOS.
- This version was created because the maintainer uses macOS in daily work. When trying to download videos from Bilibili creators, the maintainer found [DownKyi for Windows](https://github.com/leiurayer/downkyi), which is useful but does not support macOS. This project was therefore rebuilt with Avalonia UI as a cross-platform edition.

## Usage

- The application includes the .NET 6, ffmpeg, and aria2 runtime environments. No separate installation is required.
- Default download paths:
  - Windows: the `Media` folder under the application directory
  - macOS: `~/Library/Application Support/DownKyi/Media`
  - Linux: `~/.config/DownKyi/Media`

## Sponsorship

If this project is helpful to you and you would like to support its development and maintenance, feel free to donate by scanning the QR codes below. Thank you for your support!

![Alipay.png](https://s3.bzdrs.cn/downkyi/img/AliPay.png)![WeChat.png](https://s3.bzdrs.cn/downkyi/img/WechatPay.jpg)

## Disclaimer

1. This software only provides video parsing. It does not upload resources or store them on any server.
2. This software only parses content from Bilibili. It does not re-encode the parsed audio or video. Some videos may undergo limited format conversion or merging.
3. All parsed content comes from videos uploaded and shared by Bilibili creators. Copyright belongs to the original authors. Content providers and uploaders are fully responsible for the content they provide and upload.
4. **All content provided by this software is for learning and communication purposes only. Any other use without authorization from the original author is prohibited. Please delete downloaded content within 24 hours. To respect copyright, please watch the content on the original publishing platform and support the creators.**
5. The software author is not responsible for any copyright issues caused by the use of this software.
