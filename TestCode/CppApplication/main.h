#pragma once


class Cookie
{
public:
  void consume(int amount);
  Cookie();
private:
  int _amount;
};