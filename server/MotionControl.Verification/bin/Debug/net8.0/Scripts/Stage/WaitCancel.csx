Log("进入可取消等待");
await DelayAsync(30000);
throw new Exception("取消未生效，脚本不应执行到这里");
